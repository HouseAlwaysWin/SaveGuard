using SaveGuard.Services;
using Xunit;

namespace SaveGuard.Tests;

public class VdfParserTests
{
    [Fact]
    public void Parses_modern_libraryfolders_with_nested_apps()
    {
        const string vdf = """
            "libraryfolders"
            {
                "0"
                {
                    "path"		"D:\\Program Files (x86)\\Steam"
                    "label"		""
                    "apps"
                    {
                        "1145360"		"11899644249"
                        "413150"		"123"
                    }
                }
                "1"
                {
                    "path"		"E:\\SteamLibrary"
                }
            }
            """;

        var root = VdfNode.Parse(vdf);
        var libs = root.GetChild("libraryfolders");
        Assert.NotNull(libs);

        // The "0" entry's path should unescape "\\" to "\".
        Assert.Equal(@"D:\Program Files (x86)\Steam", libs!.GetChild("0")!.GetValue("path"));
        Assert.Equal(@"E:\SteamLibrary", libs.GetChild("1")!.GetValue("path"));

        // Nested apps block is reachable.
        Assert.Equal("11899644249", libs.GetChild("0")!.GetChild("apps")!.GetValue("1145360"));
    }

    [Fact]
    public void Parses_appmanifest_fields()
    {
        const string acf = """
            "AppState"
            {
                "appid"		"1145360"
                "name"		"Hades"
                "StateFlags"		"4"
                "installdir"		"Hades"
            }
            """;

        var app = VdfNode.Parse(acf).GetChild("AppState");
        Assert.NotNull(app);
        Assert.Equal("1145360", app!.GetValue("appid"));
        Assert.Equal("Hades", app.GetValue("name"));
        Assert.Equal("4", app.GetValue("StateFlags"));
        Assert.Equal("Hades", app.GetValue("installdir"));
    }

    [Fact]
    public void Keys_are_case_insensitive()
    {
        var app = VdfNode.Parse("\"AppState\" { \"AppId\" \"42\" }").GetChild("appstate");
        Assert.Equal("42", app!.GetValue("APPID"));
    }

    [Fact]
    public void Skips_line_comments()
    {
        const string vdf = """
            "AppState"
            {
                // this is a comment
                "name"		"Test"  // trailing comment
            }
            """;
        Assert.Equal("Test", VdfNode.Parse(vdf).GetChild("AppState")!.GetValue("name"));
    }

    [Fact]
    public void Handles_escaped_quote_in_value()
    {
        var app = VdfNode.Parse("\"AppState\" { \"name\" \"Say \\\"Hi\\\"\" }").GetChild("AppState");
        Assert.Equal("Say \"Hi\"", app!.GetValue("name"));
    }

    [Fact]
    public void Tolerates_malformed_input_without_throwing()
    {
        // Truncated block — must not throw, just return what parsed.
        var root = VdfNode.Parse("\"AppState\" { \"appid\" \"7\" ");
        Assert.Equal("7", root.GetChild("AppState")!.GetValue("appid"));
    }

    [Fact]
    public void Missing_keys_return_null()
    {
        var root = VdfNode.Parse("\"AppState\" { }");
        Assert.Null(root.GetChild("AppState")!.GetValue("nope"));
        Assert.Null(root.GetChild("Nonexistent"));
    }
}
