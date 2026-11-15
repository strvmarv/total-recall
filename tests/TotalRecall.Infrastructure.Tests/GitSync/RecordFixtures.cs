using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using TotalRecall.Core;
using SyncRecord = TotalRecall.Core.SyncRecord.SyncRecord;
using SyncRecordModule = TotalRecall.Core.SyncRecord;

namespace TotalRecall.Infrastructure.Tests.GitSync;

/// <summary>Shared sample records and canonical texts for the git sync codec tests.</summary>
internal static class RecordFixtures
{
    public const string Id = "3f9c2a7e-1111";
    public const string RecordPath = "memories/3f/3f9c2a7e-1111.json";
    public const string StampText = "2026-11-12T09:14:03.512Z.000003.4f2a9c1e";

    public static SyncStamp.Stamp Stamp(string text) => SyncStamp.StampModule.tryParse(text).Value;

    public static long Time(string timestamp) => SyncStamp.Timestamp.tryParse(timestamp).Value;

    public static FSharpOption<T> Some<T>(T value) => FSharpOption<T>.Some(value);

    /// <summary>A record with every parameter defaulted to the minimal canonical sample.</summary>
    public static SyncRecord Make(
        string id = Id,
        string content = "Use bun, not npm, in this repo.",
        IEnumerable<string>? tags = null,
        FSharpOption<SourceTool>? source = null,
        FSharpOption<string>? project = null,
        FSharpOption<string>? scope = null,
        FSharpOption<SyncRecordModule.Origin>? origin = null,
        FSharpOption<string>? writtenBy = null,
        Tier? tier = null,
        bool sticky = false,
        string contentStamp = StampText,
        string tierStamp = StampText,
        string stickyStamp = StampText,
        long? createdAtMs = null) =>
        new(
            id,
            content,
            EntryType.Correction,
            ListModule.OfSeq(tags ?? Array.Empty<string>()),
            source!,
            createdAtMs ?? Time("2026-11-12T09:14:03.000Z"),
            project!,
            scope!,
            new SyncRecordModule.Placement(tier ?? Tier.Warm, Stamp(tierStamp), sticky, Stamp(stickyStamp)),
            Stamp(contentStamp),
            origin!,
            writtenBy!);

    /// <summary>The minimal canonical text; <c>__CONTENT__</c> and <c>__TAGS__</c> are placeholders.</summary>
    public const string Template = """
        {
          "format": "total-recall/memory/v1",
          "id": "3f9c2a7e-1111",
          "content": "__CONTENT__",
          "entry_type": "correction",
          "tags": __TAGS__,
          "created_at": "2026-11-12T09:14:03.000Z",
          "updated_at": "2026-11-12T09:14:03.512Z",
          "ext": {
            "total-recall": {
              "placement": {
                "tier": "warm",
                "tier_stamp": "2026-11-12T09:14:03.512Z.000003.4f2a9c1e",
                "sticky": false,
                "sticky_stamp": "2026-11-12T09:14:03.512Z.000003.4f2a9c1e"
              },
              "content_stamp": "2026-11-12T09:14:03.512Z.000003.4f2a9c1e"
            }
          }
        }

        """;

    public static string Canonical(string contentJson = "Use bun, not npm, in this repo.", string tagsJson = "[]") =>
        Template.Replace("\r\n", "\n").Replace("__CONTENT__", contentJson).Replace("__TAGS__", tagsJson);

    /// <summary>The minimal sample as canonical text.</summary>
    public static string Minimal => Canonical();

    /// <summary>A canonical record with every optional field and unknown content at every level
    /// (an unknown source, unknown properties in the top level, ext, total-recall, placement and origin).</summary>
    private const string UnknownsTemplate = """
        {
          "format": "total-recall/memory/v1",
          "id": "3f9c2a7e-1111",
          "content": "Use bun, not npm, in this repo.",
          "entry_type": "correction",
          "tags": [],
          "source": "windsurf",
          "created_at": "2026-11-12T09:14:03.000Z",
          "updated_at": "2026-11-12T09:14:03.512Z",
          "ext": {
            "total-recall": {
              "project": "owner/repo",
              "placement": {
                "tier": "warm",
                "tier_stamp": "2026-11-12T09:14:03.512Z.000003.4f2a9c1e",
                "sticky": false,
                "sticky_stamp": "2026-11-12T09:14:03.512Z.000003.4f2a9c1e",
                "note": "p"
              },
              "content_stamp": "2026-11-12T09:14:03.512Z.000003.4f2a9c1e",
              "origin": {
                "tool": "claude-code",
                "hash": "h",
                "extra": 7
              },
              "written_by": "laptop",
              "future": true,
              "alpha": null,
              "obj": {},
              "arr": [],
              "nested": [
                1,
                {
                  "a": null
                }
              ],
              "n1": -0,
              "n2": 1.50,
              "n3": 1E400,
              "s": "q\"\\"
            },
            "other-tool": {
              "k": [
                1,
                2
              ]
            },
            "another-tool": {}
          },
          "zeta": {
            "n": 1.50
          },
          "yota": [],
          "alpha-top": "z"
        }

        """;

    public static string Unknowns => UnknownsTemplate.Replace("\r\n", "\n");
}
