namespace MusicFormatConverter.Core;

internal static class EncoderCapabilities
{
    internal static bool ContainsEncoder(string listing, string name) =>
        listing.Split('\n').Any(line =>
        {
            var columns = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            return columns.Length >= 2 && columns[0].Length == 6 && columns[0][0] == 'A' &&
                   columns[1] == name;
        });

    internal static async Task<bool> HasMp3EncoderAsync(EnginePaths paths, CancellationToken token)
    {
        var result = await ProcessRunner.RunAsync(paths.Ffmpeg, ["-hide_banner", "-encoders"],
            TimeSpan.FromSeconds(15), token).ConfigureAwait(false);
        if (result.ExitCode != 0) throw new AudioProcessingException("无法读取本地编码器能力，请检查转换引擎。");
        return ContainsEncoder(result.StandardOutput, "libmp3lame");
    }
}
