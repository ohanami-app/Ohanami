using System;
using Xunit;

namespace Ohanami.Core.Tests;

public sealed class CommentStripperTests
{
    [Fact]
    public void Line_and_block_comments_are_removed()
    {
        string src = "int a; // alpha\n/* beta\ngamma */ int b;\n/// <summary>delta</summary>\nint c;";
        string stripped = CommentStripper.StripCSharp(src);
        Assert.DoesNotContain("alpha", stripped);
        Assert.DoesNotContain("beta", stripped);
        Assert.DoesNotContain("gamma", stripped);
        Assert.DoesNotContain("delta", stripped);
        Assert.Contains("int a;", stripped);
        Assert.Contains("int b;", stripped);
        Assert.Contains("int c;", stripped);
    }

    [Fact]
    public void String_literals_are_kept_even_when_they_contain_comment_markers()
    {
        string src = "var u = \"http://example\"; var v = @\"a // b \"\"q\"\" \"; var w = '\"'; // tail";
        string stripped = CommentStripper.StripCSharp(src);
        Assert.Contains("\"http://example\"", stripped);
        Assert.Contains("a // b", stripped);
        Assert.Contains("'\"'", stripped);
        Assert.DoesNotContain("tail", stripped);
    }

    [Fact]
    public void Escaped_quote_does_not_end_a_string()
    {
        string src = "var s = \"a\\\"// still string\"; // gone";
        string stripped = CommentStripper.StripCSharp(src);
        Assert.Contains("still string", stripped);
        Assert.DoesNotContain("gone", stripped);
    }

    [Fact]
    public void Unclosed_block_comment_is_an_error()
    {
        Assert.Throws<FormatException>(() => CommentStripper.StripCSharp("int a; /* open"));
    }

    [Fact]
    public void Xml_comments_are_removed()
    {
        string stripped = CommentStripper.StripXml("<A><!-- note\nhere --><B/></A>");
        Assert.DoesNotContain("note", stripped);
        Assert.Contains("<B/>", stripped);
    }
}
