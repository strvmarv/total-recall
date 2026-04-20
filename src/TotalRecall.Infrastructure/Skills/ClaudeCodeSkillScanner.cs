using System.Text.RegularExpressions;

namespace TotalRecall.Infrastructure.Skills;

/// <summary>
/// Walks <c>~/.claude/skills/</c> and (optionally) <c>&lt;project&gt;/.claude/skills/</c>,
/// parses SKILL.md frontmatter, and produces client-side <see cref="ImportedSkill"/>
/// bundles that can be POSTed to cortex. Unlike the cortex-side importer this is a
/// pure scanner — no hashing, no EF, no database access. Cortex owns duplicate
/// detection via content-hashing on receipt. Plugin-cache paths under
/// <c>~/.claude/plugins/</c> are intentionally NOT scanned (spec §1.1 principle 7).
/// </summary>
public interface IClaudeCodeSkillScanner
{
    /// <param name="userId">Cortex user id; used for scope-id derivation.</param>
    /// <param name="projectPath">Optional project root for {project}/.claude/skills/.</param>
    Task<ClaudeCodeScanResult> ScanAsync(string userId, string? projectPath, CancellationToken ct);
}

public sealed record ClaudeCodeScanResult(
    IReadOnlyList<ImportedSkill> Skills,
    IReadOnlyList<ScanError> Errors);

public sealed record ScanError(string SourcePath, string Error);

public sealed class ClaudeCodeSkillScanner : IClaudeCodeSkillScanner
{
    private const int MaxDepth = 4;
    private const int MaxFilesPerBundle = 50;
    private const int BinaryCapBytes = 1_048_576;

    private static readonly string[] DefaultIgnores = new[]
    {
        ".git", "node_modules", ".DS_Store", "*.pyc", "__pycache__"
    };

    private readonly string? _homeOverride;

    public ClaudeCodeSkillScanner() : this(null) { }

    /// <summary>Test seam: override the user-home directory.</summary>
    internal ClaudeCodeSkillScanner(string? homeOverride)
    {
        _homeOverride = homeOverride;
    }

    public Task<ClaudeCodeScanResult> ScanAsync(string userId, string? projectPath, CancellationToken ct)
    {
        var skills = new List<ImportedSkill>();
        var errors = new List<ScanError>();

        var home = _homeOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var userSkills = Path.Combine(home, ".claude", "skills");
        ScanRoot(userSkills, userId, tagProject: null, skills, errors, ct);

        if (!string.IsNullOrEmpty(projectPath))
        {
            var projectSkills = Path.Combine(projectPath!, ".claude", "skills");
            if (Directory.Exists(projectSkills))
            {
                var projectName = new DirectoryInfo(projectPath!).Name;
                ScanRoot(projectSkills, userId, tagProject: projectName, skills, errors, ct);
            }
        }

        return Task.FromResult(new ClaudeCodeScanResult(skills, errors));
    }

    private static void ScanRoot(
        string root, string userId, string? tagProject,
        List<ImportedSkill> skills, List<ScanError> errors, CancellationToken ct)
    {
        if (!Directory.Exists(root)) return;

        var scope = "user";
        var scopeId = $"user:{userId}";

        // Single top-level *.md files = single-file bundles.
        foreach (var file in Directory.EnumerateFiles(root, "*.md", SearchOption.TopDirectoryOnly))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var skill = LoadSingleFile(file, scope, scopeId, tagProject);
                if (skill is not null) skills.Add(skill);
            }
            catch (Exception ex)
            {
                errors.Add(new ScanError(file, ex.Message));
            }
        }

        // Subdirectories containing SKILL.md = bundled skills.
        foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
        {
            ct.ThrowIfCancellationRequested();
            var skillMd = Path.Combine(dir, "SKILL.md");
            if (!File.Exists(skillMd)) continue;
            try
            {
                var skill = LoadBundle(dir, scope, scopeId, tagProject);
                if (skill is not null) skills.Add(skill);
            }
            catch (Exception ex)
            {
                errors.Add(new ScanError(dir, ex.Message));
            }
        }
    }

    private static ImportedSkill? LoadSingleFile(string path, string scope, string scopeId, string? tagProject)
    {
        var raw = File.ReadAllText(path);
        var (fm, body) = FrontmatterParser.Parse(raw);
        var stripped = StripBom(raw);
        if (stripped.StartsWith("---", StringComparison.Ordinal) && fm is null)
            throw new InvalidDataException($"Malformed or unterminated frontmatter in {path}");
        var name = fm?.Name ?? Path.GetFileNameWithoutExtension(path);
        var tags = BuildTags(tagProject);
        return new ImportedSkill(
            name,
            fm?.Description,
            body,
            fm?.RawJson ?? "{}",
            Array.Empty<ImportedSkillFile>(),
            path,
            scope,
            scopeId,
            tags);
    }

    private static ImportedSkill? LoadBundle(string bundleDir, string scope, string scopeId, string? tagProject)
    {
        var skillMd = Path.Combine(bundleDir, "SKILL.md");
        var raw = File.ReadAllText(skillMd);
        var (fm, body) = FrontmatterParser.Parse(raw);

        // A bundle's SKILL.md MUST carry a closed `---` frontmatter block. If the
        // file starts with `---` but the parser couldn't resolve a named block we
        // treat it as malformed and surface a ScanError up-stack. Callers catch
        // and record the failure; the bundle is dropped from the returned skills.
        var stripped = StripBom(raw);
        if (stripped.StartsWith("---", StringComparison.Ordinal) && fm is null)
            throw new InvalidDataException($"Malformed or unterminated frontmatter in {skillMd}");

        var name = fm?.Name ?? new DirectoryInfo(bundleDir).Name;

        var ignore = LoadSkillignore(bundleDir);
        var useDefaults = ignore.Count == 0;
        var files = new List<ImportedSkillFile>();
        int count = 0;

        foreach (var file in EnumerateBundleFiles(bundleDir, MaxDepth, useDefaults))
        {
            if (count >= MaxFilesPerBundle) break;
            var rel = Path.GetRelativePath(bundleDir, file).Replace('\\', '/');
            if (rel.Equals("SKILL.md", StringComparison.OrdinalIgnoreCase)) continue;
            if (!useDefaults && ignore.Any(pat => MatchesGlob(rel, pat))) continue;

            FileInfo info;
            try { info = new FileInfo(file); } catch { continue; }

            var contentType = ClassifyContent(rel);
            var content = string.Empty;
            if (info.Length > BinaryCapBytes)
            {
                contentType = "binary-ignored";
            }
            else if (contentType != "binary-ignored")
            {
                try { content = File.ReadAllText(file); } catch { continue; }
            }

            var size = (int)Math.Min(info.Length, int.MaxValue);
            files.Add(new ImportedSkillFile(rel, content, contentType, size));
            count++;
        }

        var tags = BuildTags(tagProject);
        return new ImportedSkill(
            name,
            fm?.Description,
            body,
            fm?.RawJson ?? "{}",
            files,
            bundleDir,
            scope,
            scopeId,
            tags);
    }

    private static IEnumerable<string> EnumerateBundleFiles(string bundleDir, int maxDepth, bool useDefaults)
    {
        var stack = new Stack<(string Dir, int Depth)>();
        stack.Push((bundleDir, 0));
        while (stack.Count > 0)
        {
            var (dir, depth) = stack.Pop();
            string[] files;
            string[] subdirs;
            try
            {
                files = Directory.GetFiles(dir);
                subdirs = Directory.GetDirectories(dir);
            }
            catch { continue; }

            foreach (var f in files)
            {
                if (useDefaults && MatchesDefault(Path.GetFileName(f))) continue;
                yield return f;
            }
            if (depth >= maxDepth) continue;
            foreach (var d in subdirs)
            {
                var dirName = new DirectoryInfo(d).Name;
                if (useDefaults && MatchesDefault(dirName)) continue;
                stack.Push((d, depth + 1));
            }
        }
    }

    private static bool MatchesDefault(string name)
    {
        foreach (var pat in DefaultIgnores)
        {
            if (MatchesGlob(name, pat)) return true;
        }
        return false;
    }

    private static List<string> LoadSkillignore(string dir)
    {
        var path = Path.Combine(dir, ".skillignore");
        if (!File.Exists(path)) return new();
        return File.ReadAllLines(path)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith("#"))
            .ToList();
    }

    // Simplistic glob — matches cortex; do not "improve" per spec follow-ups.
    private static bool MatchesGlob(string relPath, string pattern)
    {
        if (pattern == relPath) return true;
        if (pattern == Path.GetFileName(relPath)) return true;
        if (pattern.Contains('*'))
        {
            var regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$";
            return Regex.IsMatch(relPath, regex)
                || Regex.IsMatch(Path.GetFileName(relPath), regex);
        }
        return false;
    }

    private static string ClassifyContent(string relPath)
    {
        var ext = Path.GetExtension(relPath).ToLowerInvariant();
        return ext switch
        {
            ".md" or ".markdown" => "markdown",
            ".sh" or ".bash" or ".ps1" or ".py" or ".js" or ".ts" => "script",
            ".txt" or ".yaml" or ".yml" or ".json" or ".xml" or ".ini" or ".toml" => "text",
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".bin" or ".exe"
                or ".dll" or ".so" or ".dylib" or ".zip" or ".tar" or ".gz" => "binary-ignored",
            _ => "text"
        };
    }

    private static string StripBom(string s)
        => !string.IsNullOrEmpty(s) && s[0] == '\uFEFF' ? s.Substring(1) : s;

    private static IReadOnlyList<string> BuildTags(string? tagProject)
    {
        if (tagProject is null) return Array.Empty<string>();
        return new[] { $"project:{tagProject}" };
    }
}
