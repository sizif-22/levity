using Levity.Services;
using Xunit;

namespace Levity.Tests;

public class FolderAccessTests
{
    private const string Root = @"D:\Projects";
    private const string Home = @"C:\Users\me";

    [Theory]
    [InlineData(@"D:\Projects", true)]
    [InlineData(@"D:\Projects\app", true)]
    [InlineData(@"d:\projects\APP\src", true)]
    [InlineData(@"D:\Projects\", true)]
    [InlineData(@"D:\Projects-evil", false)]              // shares a prefix, is not inside
    [InlineData(@"D:\Project", false)]
    [InlineData(@"D:\Projects\..\Secrets", false)]        // dot-dot is resolved before the check
    [InlineData(@"D:\Projects\app\..\..\Windows", false)]
    [InlineData(@"C:\Windows", false)]
    [InlineData(@"E:\Projects\app", false)]
    public void IsInside_compares_whole_folders_after_resolving_dot_dot(string path, bool expected)
    {
        Assert.Equal(expected, FolderAccess.IsInside(path, Root));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad|path")]
    [InlineData("a\u0000b")]
    public void Garbage_is_never_inside_anything(string path)
    {
        Assert.False(FolderAccess.IsInside(path, Root));
        Assert.False(FolderAccess.Same(path, Root));
    }

    [Fact]
    public void An_ide_open_is_allowed_for_the_projects_folder_and_the_current_project_only()
    {
        Assert.True(FolderAccess.PhoneMayOpenInIde(@"D:\Projects\app", Root, @"C:\elsewhere"));
        Assert.True(FolderAccess.PhoneMayOpenInIde(@"C:\elsewhere", Root, @"C:\elsewhere"));
        Assert.True(FolderAccess.PhoneMayOpenInIde(@"c:\ELSEWHERE\", Root, @"C:\elsewhere"));
        Assert.False(FolderAccess.PhoneMayOpenInIde(@"C:\Windows\System32", Root, @"C:\elsewhere"));
        Assert.False(FolderAccess.PhoneMayOpenInIde(@"C:\elsewhere\sub", Root, @"C:\elsewhere"));   // only the project itself, not below it
        Assert.False(FolderAccess.PhoneMayOpenInIde(@"D:\Projects-evil", Root, @"C:\elsewhere"));
    }

    [Fact]
    public void A_diagram_may_use_projects_home_the_current_project_and_recent_folders()
    {
        var recent = new[] { @"E:\scratch\idea" };
        Assert.True(FolderAccess.PhoneMayUseForDiagram(@"D:\Projects\app", Root, Home, @"C:\cwd", recent));
        Assert.True(FolderAccess.PhoneMayUseForDiagram(Home, Root, Home, @"C:\cwd", recent));
        Assert.True(FolderAccess.PhoneMayUseForDiagram(@"C:\cwd", Root, Home, @"C:\cwd", recent));
        Assert.True(FolderAccess.PhoneMayUseForDiagram(@"E:\scratch\idea", Root, Home, @"C:\cwd", recent));
        Assert.False(FolderAccess.PhoneMayUseForDiagram(@"C:\Windows", Root, Home, @"C:\cwd", recent));
        Assert.False(FolderAccess.PhoneMayUseForDiagram(@"C:\Users", Root, Home, @"C:\cwd", recent));            // the parent of home is not home
        Assert.False(FolderAccess.PhoneMayUseForDiagram(@"E:\scratch", Root, Home, @"C:\cwd", recent));          // the parent of a recent folder is not recent
        Assert.False(FolderAccess.PhoneMayUseForDiagram(@"C:\Users\me\Documents", Root, Home, @"C:\cwd", recent)); // below home is not home
    }
}
