using Levity.Services;
using Xunit;

namespace Levity.Tests;

public class CommandShortcutTests
{
    [Theory]
    [InlineData("/cmd{git status}", "git status", "", false)]
    [InlineData("/cmd{$git status}", "git status", "", true)]
    [InlineData("/cmd $git status", "git status", "", true)]
    [InlineData("/cmd git status", "git status", "", false)]
    [InlineData("/cli{ls}", "ls", "", false)]
    [InlineData("/run{$dir}", "dir", "", true)]
    [InlineData("/sh{$whoami}", "whoami", "", true)]
    [InlineData("/CMD{$git status}", "git status", "", true)]
    [InlineData("  /cmd{$ls}  ", "ls", "", true)]
    [InlineData("/cmd$git status;", "git status", "", true)]
    [InlineData("/cmd$git status", "git status", "", true)]
    [InlineData("/cmd$git status ;", "git status", "", true)]
    [InlineData("  /cmd$ls;  ", "ls", "", true)]
    [InlineData("/cmd$cd app; npm install;", "cd app; npm install", "", true)]
    [InlineData("/cmd$cd app; npm install", "cd app; npm install", "", true)]
    [InlineData("/cmd$bun run dev;", "bun run dev", "", true)]
    [InlineData("/cmd$-i bun convex deploy;", "-i bun convex deploy", "", true)]
    [InlineData("/cmd$git commit -m \"a;b\";", "git commit -m \"a;b\"", "", true)]
    [InlineData("/CMD$whoami;", "whoami", "", true)]
    [InlineData("/cli$ls;", "ls", "", true)]
    [InlineData("/run$dir;", "dir", "", true)]
    [InlineData("/sh$whoami;", "whoami", "", true)]
    [InlineData("/cmd $git status;", "git status", "", true)]
    [InlineData("/cmd{$git status;}", "git status", "", true)]
    public void Parses_the_command_forms(string input, string command, string after, bool direct)
    {
        Assert.True(CommandShortcut.TryParse(input, out string c, out string a, out bool d));
        Assert.Equal(command, c);
        Assert.Equal(after, a);
        Assert.Equal(direct, d);
    }

    [Fact]
    public void Nested_braces_stay_inside_the_command()
    {
        Assert.True(CommandShortcut.TryParse("/cmd{$echo ${HOME}}", out string c, out string a, out bool d));
        Assert.Equal("echo ${HOME}", c);
        Assert.Equal("", a);
        Assert.True(d);
    }

    [Fact]
    public void Text_after_the_brace_is_a_follow_up_for_the_agent()
    {
        Assert.True(CommandShortcut.TryParse("/cmd{$npm test} summarize failures", out string c, out string a, out _));
        Assert.Equal("npm test", c);
        Assert.Equal("summarize failures", a);
    }

    [Fact]
    public void There_is_no_background_flag_so_text_after_the_brace_is_always_a_follow_up()
    {
        // A slow command moves to the Jobs panel by itself; "-b" is no longer special.
        Assert.True(CommandShortcut.TryParse("/cmd{$bun run dev} -b", out string c, out string a, out bool d));
        Assert.Equal("bun run dev", c);
        Assert.Equal("-b", a);
        Assert.True(d);
    }

    [Fact]
    public void A_dash_b_inside_the_short_form_is_just_part_of_the_command()
    {
        Assert.True(CommandShortcut.TryParse("/cmd$git status -b", out string c, out string a, out _));
        Assert.Equal("git status -b", c);
        Assert.Equal("", a);
    }

    [Theory]
    [InlineData("hello")]
    [InlineData("/cmdx{ls}")]
    [InlineData("/cmd")]
    [InlineData("/cmd{}")]
    [InlineData("/cmd{$}")]
    [InlineData("/cmd$")]
    [InlineData("/cmd$;")]
    [InlineData("/cmd$  ;  ")]
    [InlineData("/cmdx$ls;")]
    [InlineData("/cmd   ")]
    [InlineData("/plan do it")]
    [InlineData("")]
    public void Rejects_things_that_are_not_commands(string input)
    {
        Assert.False(CommandShortcut.TryParse(input, out _, out _, out _));
    }

    [Fact]
    public void Unclosed_brace_takes_the_rest_as_the_command()
    {
        Assert.True(CommandShortcut.TryParse("/cmd{$git status", out string c, out _, out bool d));
        Assert.Equal("git status", c);
        Assert.True(d);
    }

    [Fact]
    public void Expand_builds_an_instruction_with_the_exact_command_and_folder()
    {
        string text = CommandShortcut.Expand("/cmd{git log -1} then explain", @"C:\proj");
        Assert.Contains(@"C:\proj", text);
        Assert.Contains("git log -1", text);
        Assert.Contains("do not try workarounds", text);
        Assert.EndsWith("Then: then explain", text);
    }

    [Fact]
    public void Expand_leaves_ordinary_prompts_alone()
    {
        Assert.Equal("explain this repo", CommandShortcut.Expand("explain this repo", @"C:\x"));
    }
}

public class DirectShellTests
{
    private const string Cwd = @"C:\Work\proj\sub";

    [Theory]
    [InlineData("cd ..", @"C:\Work\proj")]
    [InlineData("cd ../..", @"C:\Work")]
    [InlineData("cd child", @"C:\Work\proj\sub\child")]
    [InlineData("CD child", @"C:\Work\proj\sub\child")]
    [InlineData("cd /d D:\\Other", @"D:\Other")]
    [InlineData("cd \"D:\\My Folder\"", @"D:\My Folder")]
    [InlineData("Set-Location ..", @"C:\Work\proj")]
    [InlineData("sl ..", @"C:\Work\proj")]
    [InlineData("chdir ..", @"C:\Work\proj")]
    public void Resolves_cd_targets(string command, string expected)
    {
        Assert.Equal(expected, DirectShell.ResolveCd(command, Cwd));
    }

    [Theory]
    [InlineData("cd")]
    [InlineData("cd ~")]
    public void Plain_cd_and_tilde_go_home(string command)
    {
        Assert.Equal(AppSettings.HomeFolder, DirectShell.ResolveCd(command, Cwd));
    }

    [Fact]
    public void Tilde_paths_expand_under_home()
    {
        Assert.Equal(Path.Combine(AppSettings.HomeFolder, "src"), DirectShell.ResolveCd("cd ~/src", Cwd));
    }

    [Theory]
    [InlineData("git status")]
    [InlineData("cdx foo")]
    [InlineData("echo cd ..")]
    [InlineData("cd .. && dir")]
    public void Other_commands_are_not_cd(string command)
    {
        // "cd .. && dir" is a real shell line; only a bare cd is intercepted.
        Assert.Null(DirectShell.ResolveCd(command, Cwd));
    }

}

public class ShortCommandFormTests
{
    [Fact]
    public void The_short_form_has_no_follow_up_so_text_after_a_semicolon_is_part_of_the_command()
    {
        // "/cmd$a; b" is one PowerShell line, never "run a, then ask the AI about b".
        Assert.True(CommandShortcut.TryParse("/cmd$npm test; summarize the failures", out string c, out string after, out bool direct));
        Assert.Equal("npm test; summarize the failures", c);
        Assert.Equal("", after);
        Assert.True(direct);
    }

    [Fact]
    public void Only_one_trailing_semicolon_is_the_end_marker()
    {
        Assert.True(CommandShortcut.TryParse("/cmd$echo hi;;", out string c, out _, out _));
        Assert.Equal("echo hi;", c);
    }

    [Fact]
    public void Without_the_dollar_a_trailing_semicolon_is_left_alone()
    {
        Assert.True(CommandShortcut.TryParse("/cmd git status;", out string c, out _, out bool direct));
        Assert.Equal("git status;", c);
        Assert.False(direct);
    }

    [Fact]
    public void The_old_brace_form_still_works_next_to_the_new_one()
    {
        Assert.True(CommandShortcut.TryParse("/cmd{$npm test} summarize failures", out string c, out string after, out bool direct));
        Assert.Equal("npm test", c);
        Assert.Equal("summarize failures", after);
        Assert.True(direct);
    }

    [Fact]
    public void Flags_inside_the_short_form_are_read_like_everywhere_else()
    {
        Assert.True(CommandShortcut.TryParse("/cmd$-i bun convex dev;", out string c, out _, out _));
        DirectShell.StripFlags(c, out string stripped, out bool interactive);
        Assert.Equal("bun convex dev", stripped);
        Assert.True(interactive);
    }
}
