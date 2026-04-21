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
    /// <param name="projectPath">Optional project root for {project}/.claude/skills/.</param>
    Task<ClaudeCodeScanResult> ScanAsync(string? projectPath, CancellationToken ct);
}

public sealed record ClaudeCodeScanResult(
    IReadOnlyList<ImportedSkill> Skills,
    IReadOnlyList<ScanError> Errors);

public sealed record ScanError(string SourcePath, string Error);

public sealed class ClaudeCodeSkillScanner : IClaudeCodeSkillScanner
{
    private readonly string? _homeOverride;

    public ClaudeCodeSkillScanner() : this(null) { }

    /// <summary>Test seam: override the user-home directory.</summary>
    internal ClaudeCodeSkillScanner(string? homeOverride)
    {
        _homeOverride = homeOverride;
    }

    public Task<ClaudeCodeScanResult> ScanAsync(string? projectPath, CancellationToken ct)
    {
        var skills = new List<ImportedSkill>();
        var errors = new List<ScanError>();

        var home = _homeOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var userSkills = Path.Combine(home, ".claude", "skills");
        SkillScannerCore.ScanRoot(userSkills, tagProject: null, skills, errors, ct);

        if (!string.IsNullOrEmpty(projectPath))
        {
            var projectSkills = Path.Combine(projectPath!, ".claude", "skills");
            if (Directory.Exists(projectSkills))
            {
                var projectName = new DirectoryInfo(projectPath!).Name;
                SkillScannerCore.ScanRoot(projectSkills, tagProject: projectName, skills, errors, ct);
            }
        }

        return Task.FromResult(new ClaudeCodeScanResult(skills, errors));
    }
}
