using System.Text;
using LocalEnvManager.Services;

using Xunit;

namespace LocalEnvManager.Tests;

public sealed class Utf8LineAssemblerTests
{
    [Fact]
    public void EmitsOnlyCompleteLines()
    {
        var assembler = new Utf8LineAssembler();

        Assert.Empty(assembler.Append(Encoding.UTF8.GetBytes("hello ")));
        Assert.Equal("hello ", assembler.PendingLine);

        var lines = assembler.Append(Encoding.UTF8.GetBytes("world\n"));

        Assert.Equal(["hello world"], lines);
        Assert.Equal(string.Empty, assembler.PendingLine);
    }

    [Fact]
    public void ReassemblesMultiByteCharacterSplitAcrossChunks()
    {
        var assembler = new Utf8LineAssembler();
        var bytes = Encoding.UTF8.GetBytes("汉\n");

        // The first byte is the start of a three-byte UTF-8 sequence.
        Assert.Empty(assembler.Append(bytes.AsSpan(0, 1)));
        var lines = assembler.Append(bytes.AsSpan(1));

        Assert.Equal(["汉"], lines);
    }

    [Fact]
    public void HandlesCrLf()
    {
        var assembler = new Utf8LineAssembler();

        var lines = assembler.Append(Encoding.UTF8.GetBytes("a\r\nb\r\n"));

        Assert.Equal(["a", "b"], lines);
    }

    [Fact]
    public void ResetDropsPartialState()
    {
        var assembler = new Utf8LineAssembler();
        assembler.Append(Encoding.UTF8.GetBytes("half"));

        assembler.Reset();

        Assert.Equal(string.Empty, assembler.PendingLine);
        Assert.Equal(["next"], assembler.Append(Encoding.UTF8.GetBytes("next\n")));
    }
}
