using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace TotalRecall.TestSupport.Transcripts;

/// <summary>One acceptance query of <c>tests/fixtures/transcripts/search/queries.json</c>.</summary>
/// <param name="Name">Unique name of the query.</param>
/// <param name="Query">The query text.</param>
/// <param name="Scope"><c>session</c>, <c>project</c> or <c>all</c>.</param>
/// <param name="Project">The project key (<c>alpha</c> or <c>beta</c>) of a project-scope query, else null.</param>
/// <param name="SessionFixture">The fixture whose session a session-scope query names, else null.</param>
/// <param name="IncludeAutomated">The <c>include_automated</c> argument.</param>
/// <param name="ExpectedFixture">The session expected in the top three; for kind <c>automated-excluded</c> the one that must be absent.</param>
/// <param name="Kind">How-to, compaction-summary, error, session-paraphrase, automated-excluded, automated-included, automated-session, project or since.</param>
/// <param name="Since">An optional <c>since</c> argument (ISO-8601 date or instant).</param>
public sealed record SearchQuery(
    string Name,
    string Query,
    string Scope,
    string? Project,
    string? SessionFixture,
    bool IncludeAutomated,
    string ExpectedFixture,
    string Kind,
    string? Since);

/// <summary>The sessions of a placed search corpus, the two project directories and the parsed queries.</summary>
public sealed class SearchCorpus
{
    private readonly Dictionary<string, string> _ids;
    private readonly Dictionary<string, string> _projects;

    internal SearchCorpus(
        IReadOnlyList<PlacedSession> sessions,
        Dictionary<string, string> ids,
        Dictionary<string, string> projects,
        IReadOnlyList<SearchQuery> queries)
    {
        Sessions = sessions;
        _ids = ids;
        _projects = projects;
        Queries = queries;
    }

    /// <summary>Every placed session: the hand-written ones, then the filler.</summary>
    public IReadOnlyList<PlacedSession> Sessions { get; }

    public IReadOnlyList<SearchQuery> Queries { get; }

    /// <summary>The Claude Code session id (the <c>host_session_id</c>) the fixture was placed under.</summary>
    public string SessionIdOf(string fixtureName) =>
        _ids.TryGetValue(fixtureName, out var id)
            ? id
            : throw new ArgumentException("no such session fixture: " + fixtureName, nameof(fixtureName));

    /// <summary>The working directory of a project (<c>alpha</c> or <c>beta</c>); its project key is the project name.</summary>
    public string ProjectCwd(string project) =>
        _projects.TryGetValue(project, out var cwd)
            ? cwd
            : throw new ArgumentException("no such project: " + project, nameof(project));
}

/// <summary>
/// Places the archive-search query corpus (spec section 8) in a <see cref="ClaudeFixtureHome"/>: the ten hand-written
/// sessions of <c>tests/fixtures/transcripts/search</c> plus 30 generated filler sessions, 40 in all, spread over two
/// projects (<c>alpha</c> and <c>beta</c>; the working directories do not exist, so each key is the folder name). Uses
/// only public API.
/// </summary>
public static class SearchFixtureCorpus
{
    public const string Alpha = "alpha";
    public const string Beta = "beta";

    /// <summary>The hand-written sessions and the project each is placed in.</summary>
    private static readonly (string Fixture, string Project)[] Authored =
    {
        ("deploy-staging", Alpha),
        ("flaky-test-retries", Alpha),
        ("invoice-compaction", Alpha),
        ("sqlite-busy-error", Alpha),
        ("retry-backoff", Alpha),
        ("automated-changelog-sdk", Alpha),
        ("automated-changelog-opening", Alpha),
        ("settings-date-locale", Alpha),
        ("beta-deploy-staging", Beta),
        ("beta-dns-cutover", Beta),
    };

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public static SearchCorpus Place(ClaudeFixtureHome home)
    {
        ArgumentNullException.ThrowIfNull(home);

        var projects = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [Alpha] = System.IO.Path.Combine(home.Root, "work", Alpha),
            [Beta] = System.IO.Path.Combine(home.Root, "work", Beta),
        };
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        var sessions = new List<PlacedSession>();
        var searchDir = ClaudeFixtureHome.SearchFixturesDir;

        for (var i = 0; i < Authored.Length; i++)
        {
            var (fixture, project) = Authored[i];
            var id = string.Create(CultureInfo.InvariantCulture, $"a{i + 1:D7}-0000-4000-8000-000000000000");
            var raw = File.ReadAllText(System.IO.Path.Combine(searchDir, fixture + ".jsonl"), Utf8);
            sessions.Add(Write(home, projects[project], id, home.RenderText(raw, projects[project], id)));
            ids[fixture] = id;
        }

        for (var i = 0; i < Filler.Length; i++)
        {
            var project = i % 2 == 0 ? Alpha : Beta;
            var id = string.Create(CultureInfo.InvariantCulture, $"f{i + 1:D7}-0000-4000-8000-000000000000");
            var (question, answer, tip) = Filler[i];
            var raw = FillerSession(question, answer, tip);
            sessions.Add(Write(home, projects[project], id, home.RenderText(raw, projects[project], id)));
            ids[string.Create(CultureInfo.InvariantCulture, $"filler-{i + 1:D2}")] = id;
        }

        return new SearchCorpus(sessions, ids, projects, ReadQueries(searchDir));
    }

    private static PlacedSession Write(ClaudeFixtureHome home, string cwd, string id, byte[] content)
    {
        var dir = System.IO.Path.Combine(home.ProjectsDir, ClaudeFixtureHome.EncodeDir(cwd));
        Directory.CreateDirectory(dir);
        var session = new PlacedSession(id, System.IO.Path.Combine(dir, id + ".jsonl"), cwd);
        home.Write(session, content);
        return session;
    }

    private static IReadOnlyList<SearchQuery> ReadQueries(string searchDir)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(System.IO.Path.Combine(searchDir, "queries.json"), Utf8));
        var list = new List<SearchQuery>();
        foreach (var q in doc.RootElement.GetProperty("queries").EnumerateArray())
        {
            list.Add(new SearchQuery(
                Name: q.GetProperty("name").GetString()!,
                Query: q.GetProperty("query").GetString()!,
                Scope: q.GetProperty("scope").GetString()!,
                Project: Optional(q, "project"),
                SessionFixture: Optional(q, "sessionFixture"),
                IncludeAutomated: q.TryGetProperty("includeAutomated", out var ia) && ia.ValueKind == JsonValueKind.True,
                ExpectedFixture: q.GetProperty("expectedFixture").GetString()!,
                Kind: q.GetProperty("kind").GetString()!,
                Since: Optional(q, "since")));
        }
        return list;
    }

    private static string? Optional(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // ---- filler ------------------------------------------------------------------------------------------------

    /// <summary>A short two-exchange dialogue as Claude Code JSONL with placeholders.</summary>
    private static string FillerSession(string question, string answer, string tip)
    {
        var sb = new StringBuilder();
        var n = 0;
        string? prev = null;

        void Line(string type, string messageJson, int seconds, bool human)
        {
            n++;
            var uuid = "00000000-0000-4000-8000-" + n.ToString("D12", CultureInfo.InvariantCulture);
            sb.Append("{\"parentUuid\":").Append(prev is null ? "null" : "\"" + prev + "\"")
              .Append(",\"isSidechain\":false,");
            if (type == "user") sb.Append("\"promptId\":\"p").Append(n).Append("\",");
            sb.Append("\"type\":\"").Append(type).Append("\",\"message\":").Append(messageJson).Append(',');
            if (human) sb.Append("\"origin\":{\"kind\":\"human\"},");
            sb.Append("\"uuid\":\"").Append(uuid).Append("\",\"timestamp\":\"{{T+").Append(seconds)
              .Append("}}\",\"userType\":\"external\",\"entrypoint\":\"cli\",\"cwd\":\"{{CWD}}\",\"sessionId\":\"{{ID}}\"," +
                      "\"version\":\"2.1.0\",\"gitBranch\":\"main\"}\n");
            prev = uuid;
        }

        string User(string text) => "{\"role\":\"user\",\"content\":" + Json(text) + "}";
        string Assistant(string text) =>
            "{\"model\":\"fixture-model\",\"id\":\"msg_fx" + (n + 1).ToString(CultureInfo.InvariantCulture) +
            "\",\"type\":\"message\",\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":" + Json(text) +
            "}],\"stop_reason\":\"end_turn\"}";

        Line("user", User(question), 10, human: true);
        Line("assistant", Assistant(answer), 12, human: false);
        Line("user", User("Is there anything else worth knowing?"), 60, human: true);
        Line("assistant", Assistant(tip), 62, human: false);
        return sb.ToString();
    }

    private static string Json(string value) =>
        "\"" + JsonEncodedText.Encode(value, JavaScriptEncoder.UnsafeRelaxedJsonEscaping) + "\"";

    /// <summary>
    /// Thirty first questions, answers and follow-up tips on cooking, gardening, hiking, personal finances, music and a
    /// few coding topics. None of them touches the topics of the queries.
    /// </summary>
    private static readonly (string Question, string Answer, string Tip)[] Filler =
    {
        // cooking
        ("How long should I boil an egg for a soft yolk?",
         "Lower the egg into boiling water and cook it for six and a half minutes, then move it to cold water so it stops cooking.",
         "Eggs straight from the fridge need about thirty seconds more than eggs kept at room temperature."),
        ("What is the secret to a crisp pizza base at home?",
         "Heat the oven as high as it goes with a baking stone or a heavy tray inside, and slide the pizza onto the hot surface.",
         "Stretch the dough by hand instead of rolling it so the air bubbles stay in the crust."),
        ("How do I stop rice from turning sticky?",
         "Rinse the rice until the water runs clear, then use one and a half cups of water for every cup of rice and keep the lid on.",
         "Let the pot rest off the heat for ten minutes before you lift the lid."),
        ("Can you suggest a quick weeknight pasta sauce?",
         "Soften garlic in olive oil, add a tin of chopped tomatoes and a pinch of salt, and simmer for fifteen minutes while the pasta cooks.",
         "Save a cup of the pasta water: a splash of it makes the sauce cling to the noodles."),
        ("How do I keep fresh herbs from wilting?",
         "Trim the stems, stand soft herbs like basil in a glass of water on the counter and keep woody herbs like thyme wrapped in a damp towel in the fridge.",
         "Basil goes black in the cold, so never put it in the fridge."),
        // gardening
        ("When should I plant tomatoes outdoors?",
         "Wait until the nights stay above ten degrees, usually a couple of weeks after the last frost, and harden the seedlings off for a week first.",
         "Bury the stem up to the first leaves; the buried part grows extra roots."),
        ("Why are the leaves on my lemon tree turning yellow?",
         "Yellow leaves usually mean overwatering or a lack of iron and nitrogen. Let the soil dry out between waterings and feed with a citrus fertiliser.",
         "Keep the pot where it gets at least six hours of sun a day."),
        ("How do I start a compost heap?",
         "Layer green waste such as vegetable scraps with brown waste such as dry leaves and cardboard, keep it damp like a wrung-out sponge and turn it every few weeks.",
         "Chop big pieces small; they break down much faster."),
        ("What can I grow in a shady corner of the garden?",
         "Ferns, hostas and hydrangeas do well in shade, and so do leafy crops like spinach and lettuce.",
         "Pale walls nearby reflect light and help a shady corner a little."),
        ("How often should I water a lawn in summer?",
         "Water deeply twice a week in the early morning rather than a little every day, so the roots grow down instead of staying shallow.",
         "Leave the grass a bit longer in hot weather; it shades the soil."),
        // hiking
        ("What should I pack for a full day hike in the mountains?",
         "Carry two litres of water, food for the whole day, a rain jacket, a warm layer, a map, a torch and a small first aid kit.",
         "Tell someone your route and the time you expect to be back."),
        ("How do I prevent blisters on a long walk?",
         "Wear boots that are broken in, choose wool socks, and stop to cover any hot spot with tape as soon as you feel it.",
         "Keep your toenails short; long nails rub against the shoe."),
        ("How do I read contour lines on a trail map?",
         "Lines close together mean a steep slope and lines far apart mean gentle ground. Each line marks a fixed step in height shown in the map key.",
         "A closed ring of lines is usually a summit, or a hollow if the numbers fall inward."),
        ("What is a sensible pace for a beginner on a steep climb?",
         "Slow down until you can still talk in full sentences. Short steps and regular small breaks beat sprinting and stopping.",
         "Trekking poles take a surprising amount of load off the knees on the way down."),
        ("What should I do if the weather turns bad on a ridge?",
         "Drop below the ridge line as soon as you can, put on your waterproofs and avoid isolated trees and high points if there is lightning.",
         "Check the forecast for the afternoon, because mountain storms often build after midday."),
        // personal finances
        ("How big should an emergency fund be?",
         "A common rule is three to six months of essential spending, kept in an easy-access savings account.",
         "Start small: even one month of costs gives you real breathing room."),
        ("What is the difference between a Roth and a traditional retirement account?",
         "A traditional account gives you a tax break now and taxes the withdrawals later, while a Roth takes the tax now and lets you withdraw tax free.",
         "If you expect a higher income later in life, the Roth is often the better choice."),
        ("How do I start budgeting without a spreadsheet?",
         "Split your take-home pay into needs, wants and savings, around fifty, thirty and twenty percent, and check the totals once a month in your banking app.",
         "Set up an automatic transfer to savings on payday so you never see that money."),
        ("Is it better to pay off a loan early or invest the money?",
         "Compare the loan's interest rate with the return you can realistically expect. A high rate loan is a guaranteed return when you pay it off.",
         "Make sure the loan has no early repayment fee before you send extra money."),
        ("How does compound interest work?",
         "You earn interest on your original money and also on the interest already added, so the balance grows faster the longer it stays untouched.",
         "Starting ten years earlier usually matters more than saving a little extra each month."),
        // music
        ("How do I tune a guitar by ear?",
         "Tune the low string to a reference note, then press the fifth fret of each string to match the open string next to it, except between the third and second strings, where it is the fourth fret.",
         "A clip-on tuner is handy in a noisy room."),
        ("What scales should a piano beginner learn first?",
         "Begin with C major, since it uses only white keys, then G major and F major, playing each slowly with the correct fingering.",
         "Practise with a metronome set slow enough that you never stumble."),
        ("How do I read a chord chart for a song?",
         "Each letter is a chord to play for the bar it sits over, and a slash means keep playing the same chord. Minor chords are written with a small m.",
         "Learn the four chords G, C, D and E minor and you can play hundreds of songs."),
        ("What is a good way to learn a song by ear?",
         "Loop a few seconds at a time, hum the melody, then find the first note on your instrument and build outward from it.",
         "Slow the recording to half speed without changing the pitch if your player allows it."),
        ("How should I practise drum rudiments?",
         "Start with single strokes and double strokes on a practice pad at a slow tempo, and raise the tempo only when every stroke is even.",
         "Keep your wrists loose; tension is what stops people getting faster."),
        // coding
        ("What does a CSS grid do that flexbox does not?",
         "A grid lays out items in rows and columns at once, while flexbox works along a single direction. Use a grid for page layouts.",
         "The fr unit divides the free space of a grid into equal shares."),
        ("How do I squash my last three commits in git?",
         "Run an interactive rebase on the last three commits, keep the first as pick and mark the other two as squash, then write one message.",
         "Only rewrite commits that you have not shared with other people."),
        ("What is a Python list comprehension?",
         "It builds a list in one line, for example squares = [n * n for n in range(10)], and can include a condition at the end.",
         "If the expression gets long, a normal for loop is easier to read."),
        ("How do I make a Docker image smaller?",
         "Start from a slim base image, combine commands to cut the number of layers, and use a multi-stage build so build tools stay out of the final image.",
         "A dockerignore file keeps junk out of the build context."),
        ("What is the difference between a left join and an inner join in SQL?",
         "An inner join returns only rows that match on both sides, while a left join keeps every row from the left table and fills the gaps with nulls.",
         "Filtering on a right-table column in the where clause turns a left join back into an inner join."),
    };
}
