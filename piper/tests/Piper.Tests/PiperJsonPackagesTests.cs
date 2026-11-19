namespace Piper.Tests;

/// <summary>
/// <c>piper package add</c> and <c>remove</c>: piper.json edited as text, only the package's entry changing, so its
/// comments and layout stay; and the newest version picked from what <c>dotnet package search</c> found.
/// </summary>
public class PiperJsonPackagesTests
{
    [Fact]
    public void A_package_is_added_to_the_packages_there_are_keeping_the_files_comments_and_layout()
    {
        Assert.Equal("""
            {
              // the app
              "name": "app",
              "packages": { "Humanizer.Core": "2.14.1", "Dapper": "2.1.66" } // used by the reports
            }
            """, PiperJsonPackages.Add("""
            {
              // the app
              "name": "app",
              "packages": { "Humanizer.Core": "2.14.1" } // used by the reports
            }
            """, "Dapper", "2.1.66"));

        // On lines of their own: the new one is too.
        Assert.Equal("""
            {
              "packages": {
                "Humanizer.Core": "2.14.1",
                "Dapper": "2.1.66"
              }
            }
            """, PiperJsonPackages.Add("""
            {
              "packages": {
                "Humanizer.Core": "2.14.1"
              }
            }
            """, "Dapper", "2.1.66"));

        Assert.Equal("""{ "packages": { "Dapper": "2.1.66" } }""", PiperJsonPackages.Add("""{ "packages": {} }""", "Dapper", "2.1.66"));
    }

    [Fact]
    public void A_package_already_there_has_its_version_changed_matched_without_regard_to_case()
    {
        // Only the version changes: the name stays as written.
        Assert.Equal("""{ "packages": { "Dapper": "2.1.66", "humanizer.core": "3.0.1" } }""",
            PiperJsonPackages.Add("""{ "packages": { "Dapper": "2.1.66", "humanizer.core": "2.14.1" } }""", "Humanizer.Core", "3.0.1"));
    }

    [Fact]
    public void Packages_are_added_to_a_file_without_them_or_to_no_file()
    {
        // A comment ending the last value's line stays with it.
        Assert.Equal("""
            {
              "name": "app",
              "plugins": ["web"], // the web
              "packages": { "Dapper": "2.1.66" }
            }
            """, PiperJsonPackages.Add("""
            {
              "name": "app",
              "plugins": ["web"] // the web
            }
            """, "Dapper", "2.1.66"));
        Assert.Equal("{\n  \"packages\": { \"Dapper\": \"2.1.66\" }\n}", PiperJsonPackages.Add("{}", "Dapper", "2.1.66"));
        Assert.Equal("{\n  \"packages\": { \"Dapper\": \"2.1.66\" }\n}\n", PiperJsonPackages.Add("", "Dapper", "2.1.66"));
        Assert.Equal("""{ "packages": { "X": "1.0.0+build.5" } }""", PiperJsonPackages.Add("""{ "packages": {} }""", "X", "1.0.0+build.5"));
    }

    [Fact]
    public void A_package_is_removed_with_its_comma()
    {
        const string Three = """{ "packages": { "A": "1.0.0", "B": "2.0.0", "C": "3.0.0" } }""";
        Assert.Equal("""{ "packages": { "B": "2.0.0", "C": "3.0.0" } }""", PiperJsonPackages.Remove(Three, "a"));
        Assert.Equal("""{ "packages": { "A": "1.0.0", "C": "3.0.0" } }""", PiperJsonPackages.Remove(Three, "B"));
        Assert.Equal("""{ "packages": { "A": "1.0.0", "B": "2.0.0" } }""", PiperJsonPackages.Remove(Three, "C"));
        Assert.Equal("""{ "name": "x", "packages": {} }""", PiperJsonPackages.Remove("""{ "name": "x", "packages": { "A": "1.0.0" } }""", "A"));
        Assert.Null(PiperJsonPackages.Remove(Three, "D"));
        Assert.Null(PiperJsonPackages.Remove("""{ "name": "x" }""", "A"));
    }

    private const string Search = """
        {
          "version": 2,
          "problems": [],
          "searchResult": [
            { "sourceName": "nuget.org", "packages": [
              { "id": "Humanizer.Core", "version": "2.9.9" },
              { "id": "Humanizer.Core", "version": "2.14.1" },
              { "id": "Humanizer.Core", "version": "3.0.0-beta.54" },
              { "id": "Humanizer.Core", "version": "2.10.0" }
            ] },
            { "sourceName": "company", "packages": [
              { "id": "humanizer.core", "version": "2.14.0" }
            ] }
          ]
        }
        """;

    [Fact]
    public void The_newest_stable_version_is_chosen_from_every_source_by_number()
    {
        Assert.Equal("2.14.1", PiperJsonPackages.Latest(Search, "Humanizer.Core", prerelease: false).Version);
        Assert.Equal("3.0.0-beta.54", PiperJsonPackages.Latest(Search, "Humanizer.Core", prerelease: true).Version);
        Assert.Null(PiperJsonPackages.Latest(Search, "Dapper", prerelease: false).Version);
        var (none, problems) = PiperJsonPackages.Latest("""{ "version": 2, "problems": [{ "text": "Unable to load the service index for source x." }], "searchResult": [] }""", "A", false);
        Assert.Null(none);
        Assert.Equal(["Unable to load the service index for source x."], problems);
    }
}
