using MusicFormatConverter.Cli;
using MusicFormatConverter.Core;

namespace MusicFormatConverter.Cli.Tests;

public class CliArgumentsTests
{
    [Theory]
    [InlineData("mp3", 3)]
    [InlineData("flac", 4)]
    [InlineData("wav", 5)]
    public void Optional_output_formats_are_explicit(string mode, int expected)
    {
        var args = CliArguments.Parse(["convert", "--output", "out", "--mode", mode, "file"]);
        Assert.Equal(expected, (int)args.Options.Mode);
    }
    [Fact]
    public void Preview_defaults_are_safe()
    {
        var args = CliArguments.Parse(["preview", "--output", "out", "输入文件夹"]);
        Assert.Equal("preview", args.Command);
        Assert.Equal(ConversionMode.Smart, args.Options.Mode);
        Assert.True(args.Options.VerifyAudio);
        Assert.Null(args.ReportDirectory);
        Assert.Equal(["输入文件夹"], args.Inputs);
    }

    [Theory]
    [InlineData("unknown", "--output", "out", "file")]
    [InlineData("convert", "file")]
    [InlineData("convert", "--output", "out")]
    [InlineData("convert", "--output", "out", "--mode", "invalid", "file")]
    [InlineData("convert", "--what", "file")]
    public void Bad_arguments_are_not_silently_ignored(params string[] args) =>
        Assert.Throws<ArgumentException>(() => CliArguments.Parse(args));

    [Fact]
    public void Explicit_mode_and_double_dash_supported()
    {
        var args = CliArguments.Parse(["convert", "--output", "out", "--mode", "aac", "--flat", "--", "-音乐.flac"]);
        Assert.Equal(ConversionMode.Aac, args.Options.Mode);
        Assert.False(args.Options.PreserveFolders);
        Assert.Equal(["-音乐.flac"], args.Inputs);
    }
}
