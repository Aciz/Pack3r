﻿using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Pack3r.Extensions;
using Pack3r.IO;
using Pack3r.Logging;
using Pack3r.Models;
using Pack3r.Progress;

namespace Pack3r.Parsers;

public interface IShaderParser
{
    Task<Dictionary<ReadOnlyMemory<char>, Shader>> GetReferencedShaders(
        Map map,
        CancellationToken cancellationToken);

    IAsyncEnumerable<Shader> Parse(
        IAsset asset,
        CancellationToken cancellationToken);
}

public class ShaderParser(
    ILineReader reader,
    PackOptions options,
    ILogger<ShaderParser> logger,
    IProgressManager progressManager)
    : IShaderParser
{
    public async Task<Dictionary<ReadOnlyMemory<char>, Shader>> GetReferencedShaders(
        Map map,
        CancellationToken cancellationToken)
    {
        ConcurrentDictionary<ReadOnlyMemory<char>, Shader> allShaders = new(ROMCharComparer.Instance);
        ConcurrentDictionary<ReadOnlyMemory<char>, List<Shader>> duplicateShaders = new(ROMCharComparer.Instance);

        var whitelistsBySource = await ParseShaderLists(map, cancellationToken);
        int shaderFileCount = 0;

        using (var progress = progressManager.Create("Parsing shader files", max: null))
        {
            await Parallel.ForEachAsync(map.AssetSources, cancellationToken, async (source, ct) =>
            {
                var whitelist = whitelistsBySource.GetValueOrDefault(source);

                bool skipPredicate(string name)
                {
                    progress.Report(Interlocked.Increment(ref shaderFileCount));

                    string fileName = Path.GetFileNameWithoutExtension(name);

                    if (whitelist is not null &&
                        !fileName.StartsWithF("levelshots") &&
                        !whitelist.Contains(fileName.AsMemory()))
                    {
                        if (options.ShaderDebug)
                            logger.Debug($"Skipped parsing shaders from {getName()} (not in shaderlist)");
                        return true;
                    }

                    if (fileName.EqualsF("q3shadersCopyForRadiant"))
                    {
                        if (options.ShaderDebug)
                            logger.Debug($"Skipped parsing Radiant specific file {getName()}");
                        return true;
                    }

                    if (fileName.StartsWithF("q3map_") || fileName.StartsWithF("q3map2_"))
                    {
                        if (options.ShaderDebug)
                            logger.Debug($"Skipped shader parsing from compiler generated file '{getName()}'");
                        return true;
                    }

                    return false;

                    string getName() => Path.Combine(source.RootPath, "scripts", name).NormalizePath();
                }

                await foreach (var shader in source.EnumerateShaders(this, skipPredicate, ct))
                {
                    allShaders.AddOrUpdate(
                        key: shader.Name,
                        addValueFactory: static (_, tuple) => tuple.shader,
                        updateValueFactory: static (_, a, tuple) =>
                        {
                            var (b, logger, map, duplicate, options) = tuple;

                            int cmp = map.AssetSources.IndexOf(a.Source).CompareTo(map.AssetSources.IndexOf(b.Source));

                            if (cmp != 0)
                            {
                                var toReturn = cmp > 0 ? a : b;
                                var other = cmp > 0 ? b : a;

                                if (options.ShaderDebug)
                                {
                                    logger.Debug($"Shader {a.Name} resolved from '{toReturn.Source}' instead of '{other.Source}'");
                                }

                                return toReturn;
                            }

                            if (!a.Source.IsExcluded || !b.Source.IsExcluded)
                            {
                                duplicate.AddOrUpdate(
                                    key: a.Name,
                                    addValueFactory: static (_, arg) => [arg.a, arg.b],
                                    updateValueFactory: static (_, existing, arg) => [arg.a, arg.b, .. existing],
                                    (a, b));
                            }
                            return a;
                        },
                        factoryArgument: (shader, logger, map, duplicateShaders, options));
                }
            }).ConfigureAwait(false);
        }

        logger.Debug($"Parsed total of {allShaders.Count} shaders");

        Dictionary<ReadOnlyMemory<char>, Shader> included = new(ROMCharComparer.Instance);

        AddShaders(
            map,
            map.Shaders,
            allShaders,
            duplicateShaders,
            included,
            cancellationToken);

        // should not happen, duplicateShaders must be empty at this point
        foreach (var (name, duplicates) in duplicateShaders)
        {
            var display = string.Join(", ", duplicates.Select(s => $"'{s.GetAbsolutePath()}'"));
            logger.Warn($"Shader '{name}' found in multiple sources: {display}");
        }

        return included;
    }

    private void AddShaders(
        Map map,
        IEnumerable<ReadOnlyMemory<char>> shaders,
        ConcurrentDictionary<ReadOnlyMemory<char>, Shader> allShaders,
        ConcurrentDictionary<ReadOnlyMemory<char>, List<Shader>> duplicateShaders,
        Dictionary<ReadOnlyMemory<char>, Shader> included,
        CancellationToken cancellationToken)
    {
        foreach (var name in shaders)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (included.ContainsKey(name))
                continue;

            if (!allShaders.TryGetValue(name, out Shader? shader))
            {
                included.Remove(name);
                continue;
            }

            included.Add(name, shader);

            if (duplicateShaders.TryRemove(name, out List<Shader>? duplicates))
            {
                var display = string.Join(", ", duplicates.Select(s => $"'{s.GetAbsolutePath()}'"));
                logger.Warn($"Shader '{name}' found in multiple sources: {display}");
            }

            if (shader.Shaders.Count != 0)
            {
                AddShaders(
                    map,
                    shader.Shaders,
                    allShaders,
                    duplicateShaders,
                    included,
                    cancellationToken);
            }
        }
    }

    public async IAsyncEnumerable<Shader> Parse(
        IAsset asset,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        State state = State.None;

        Shader? shader = null;
        ReadOnlyMemory<char> token;
        bool inComment = false;

        await foreach (var line in reader.ReadLines(asset, default, cancellationToken).ConfigureAwait(false))
        {
            if (inComment)
            {
                if (line.FirstChar == '*' && line.Value.Length == 2 && line.Value.Span[1] == '/')
                {
                    inComment = false;
                }
                continue;
            }

            if (line.FirstChar == '/' && line.Value.Length == 2 && line.Value.Span[1] == '*')
            {
                inComment = true;
                continue;
            }

            if (state == State.AfterShaderName)
            {
                if (!line.IsOpeningBrace)
                {
                    throw new InvalidDataException(
                        $"Expected {{ on line {line.Index} in file '{asset.FullPath}', but line was: {line.Raw}");
                }

                state = State.Shader;
                continue;
            }

            if (state == State.None)
            {
                ReadOnlyMemory<char> shaderName = line.Value;
                State next = State.AfterShaderName;

                // handle opening brace left on the previous line, e.g:
                // textures/mymap/ice {
                if (shaderName.Span[^1] == '{')
                {
                    shaderName = shaderName[..^1];
                    next = State.Shader;
                }

                if (shaderName.Span.ContainsAny(Tokens.Braces))
                {
                    throw new InvalidDataException(
                        $"Expected shader name on line {line.Index} in file '{asset.FullPath}', got: '{line.Raw}'");
                }

                shader = new Shader(shaderName, asset);
                state = next;
                continue;
            }

            Debug.Assert(shader != null);

            if (state == State.Stage)
            {
                if (line.IsOpeningBrace)
                {
                    throw new InvalidDataException(
                        $"Invalid token '{line.Raw}' on line {line.Index} in file {asset.FullPath}");
                }

                if (line.IsClosingBrace)
                {
                    state = State.Shader;
                    continue;
                }

                // only map, animmap, clampmap and videomap are valid
                if ((line.FirstChar | 0x20) is not ('m' or 'a' or 'c' or 'v'))
                {
                    continue;
                }

                if (line.MatchPrefix("map ", out token) ||
                    line.MatchPrefix("clampMap ", out token))
                {
                    // $lightmap, $whiteimage etc
                    if (token.Span[0] != '$')
                    {
                        shader.Resources.Add(token);
                    }
                }
                else if (line.MatchPrefix("animMap ", out token))
                {
                    // read past the frames-agument
                    if (token.TryReadPastWhitespace(out token))
                    {
                        foreach (var range in token.Split(' '))
                            shader.Resources.Add(token[range]);
                    }
                    else
                    {
                        logger.UnparsableKeyword(asset.FullPath, line.Index, "animMap", line.Raw);
                    }
                }
                else if (line.MatchPrefix("videomap ", out token))
                {
                    shader.Resources.Add(token);
                }

                continue;
            }

            if (state == State.Shader)
            {
                if (line.IsOpeningBrace)
                {
                    state = State.Stage;
                    continue;
                }

                if (line.IsClosingBrace)
                {
                    Debug.Assert(shader != null);
                    yield return shader;
                    state = State.None;
                    continue;
                }

                // early exit for known irrelevant keywords
                if (CanSkipShaderDirective(line.Value.Span))
                    continue;

                bool found = false;

                foreach (var prefix in _simpleShaderRefPrefixes)
                {
                    if (line.MatchPrefix(prefix, out token))
                    {
                        if (!token.Span.StartsWith("$", StringComparison.Ordinal))
                            shader.Shaders.Add(token);
                        found = true;
                        break;
                    }
                }

                if (!found && options.IncludeSource)
                {
                    foreach (var prefix in _devTexturePrefixes)
                    {
                        if (line.MatchPrefix(prefix, out token))
                        {
                            if (!token.Span.StartsWith("$", StringComparison.Ordinal))
                            {
                                shader.Resources.Add(token.Trim('"')); // netradiant allows using doublequotes here
                            }

                            found = true;
                            break;
                        }
                    }
                }

                if (found)
                    continue;

                if (line.MatchPrefix("implicit", out token))
                {
                    if (!token.TryReadPastWhitespace(out token))
                    {
                        logger.Warn($"Missing implicit mapping path on line {line.Index} in shader '{shader.Name}' in file '{asset.FullPath}'");
                    }
                    else
                    {
                        if (token.Span.Equals("-", StringComparison.Ordinal))
                        {
                            shader.ImplicitMapping = shader.Name;
                        }
                        else
                        {
                            shader.ImplicitMapping = token;
                        }
                    }
                }
                else if (line.MatchPrefix("skyparms ", out token))
                {
                    if (!token.TryReadUpToWhitespace(out token))
                    {
                        logger.UnparsableKeyword(asset.FullPath, line.Index, "skyparms", line.Raw);
                        continue;
                    }

                    if (token.Span.Equals("-", StringComparison.Ordinal))
                    {
                        token = shader.Name;
                    }

                    foreach (var suffix in _skySuffixes)
                    {
                        shader.Resources.Add($"{token}{suffix}".AsMemory());
                    }
                }
                else if (line.MatchPrefix("sunshader ", out token))
                {
                    shader.Shaders.Add(token);
                }
                else if (line.MatchPrefix("q3map_surfaceModel ", out token))
                {
                    if (token.TryReadUpToWhitespace(out token))
                    {
                        shader.Resources.Add(token);
                    }
                    else
                    {
                        logger.UnparsableKeyword(asset.FullPath, line.Index, "q3map_surfaceModel", line.Raw);
                    }
                }
                else if (!shader.HasLightStyles && line.MatchPrefix("q3map_lightstyle ", out _))
                {
                    shader.HasLightStyles = true;
                }
            }
        }

        if (state != State.None)
        {
            logger.Fatal($"Shader '{asset.FullPath}' ended in an invalid state: {state}");
            throw new ControlledException();
        }
    }

    private async Task<Dictionary<AssetSource, HashSet<ReadOnlyMemory<char>>>> ParseShaderLists(
        Map map,
        CancellationToken cancellationToken)
    {
        if (!options.UseShaderlist)
            return [];

        var dict = new Dictionary<AssetSource, HashSet<ReadOnlyMemory<char>>>();

        foreach (var source in map.AssetSources)
        {
            var shaderlist = source.GetShaderlist();

            if (shaderlist is null)
                continue;

            try
            {
                if (!shaderlist.Exists)
                {
                    logger.Debug($"Shaderlist skipped, does not exist in {shaderlist.FullName}");
                    continue;
                }

                HashSet<ReadOnlyMemory<char>> allowedFiles = new(ROMCharComparer.Instance);

                await foreach (var line in reader.ReadLines(shaderlist.FullName, default, cancellationToken))
                {
                    allowedFiles.Add(line.Value);
                }

                dict.Add(source, allowedFiles);
            }
            catch (Exception e)
            {
                logger.Exception(e, $"Could not read shaderlist '{shaderlist.FullName}'");
                throw new ControlledException();
            }
        }

        return dict;
    }

    private static readonly ImmutableArray<string> _skySuffixes =
    [
        "_bk", "_dn", "_ft", "_up", "_rt", "_lf"
    ];

    private static readonly ImmutableArray<string> _simpleShaderRefPrefixes =
    [
        "q3map_backShader",
        "q3map_baseShader",
        "q3map_cloneShader",
        "q3map_remapShader",
    ];

    private static readonly ImmutableArray<string> _devTexturePrefixes =
    [
        "q3map_lightImage",
        "qer_editorImage",
        "q3map_normalImage",
    ];

    private bool CanSkipShaderDirective(ReadOnlySpan<char> line)
    {
        // shouldn't happen
        if (line.IsEmpty)
            return true;

        return (line[0] | 0x20) switch
        {
            'q' => !options.IncludeSource && line.StartsWith("qer_"),
            's' => line.StartsWith("surfaceparm "),
            'c' => line.StartsWith("cull "),
            'n' => line.StartsWith("nopicmip") || line.StartsWith("nomipmaps"),
            't' => line.StartsWith("tesssize"),
            _ => false,
        };
    }

    private enum State : byte
    {
        /// <summary>Top-level</summary>
        None = 0,

        /// <summary>Shader name read but first brace is not</summary>
        AfterShaderName = 1,

        /// <summary>In shader, e.g. qer_editorimage</summary>
        Shader = 2,

        /// <summary>In a stage e.g. map $lightmap</summary>
        Stage = 3,
    }
}
