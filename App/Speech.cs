using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.SpeechSynthesis;
using Windows.Storage.Streams;
using Legado.UI3.Core;

namespace Legado.UI3;

public sealed partial class MainWindow
{
    private CancellationTokenSource? speech;
    private MediaPlayer? speechPlayer;

    private Task ToggleSpeechAsync()
    {
        if (speech is not null) { StopSpeech(); return Task.CompletedTask; }
        if (readerBook is null || readerBusy || readingToken == "") return Task.CompletedTask;
        var chapter = readingIndex;
        var session = readerSession;
        var cancellation = new CancellationTokenSource();
        speech = cancellation;
        readerSpeech!.Content = "停止朗读";
        _ = RunAsync(() => SpeakChapterAsync(chapter, session, cancellation));
        return Task.CompletedTask;
    }
    private void StopSpeech()
    {
        var running = speech;
        speech = null;
        running?.Cancel();
        speechPlayer?.Pause();
        if (readerSpeech is not null) readerSpeech.Content = "朗读";
        if (readerWeb?.CoreWebView2 is not null && readingToken != "") _ = ReaderScriptAsync("highlight", JsonValue.Create(readingIndex), JsonValue.Create(-1));
    }
    private SpeechSynthesizer CreateSpeechSynthesizer()
    {
        var preferences = store.Settings["tts"];
        var synthesizer = new SpeechSynthesizer();
        var voice = SpeechSynthesizer.AllVoices.FirstOrDefault(v => v.Id == preferences.S("voiceURI"))
            ?? SpeechSynthesizer.AllVoices.FirstOrDefault(v => v.Language.StartsWith("zh", StringComparison.OrdinalIgnoreCase));
        if (voice is not null) synthesizer.Voice = voice;
        synthesizer.Options.SpeakingRate = Math.Clamp(preferences.D("rate", 1), .5, 2);
        synthesizer.Options.AudioPitch = Math.Clamp(preferences.D("pitch", 1), .5, 2);
        return synthesizer;
    }
    private HttpVoice? SelectedHttpVoice()
    {
        var id = store.Settings["tts"].S("voiceURI");
        if (!id.StartsWith("http:", StringComparison.Ordinal)) return null;
        return voiceLibrary.Load().FirstOrDefault(v => v.Id == id) ?? throw new IOException("此网络语音未在本机配置，请重新导入或切换音色");
    }
    private async Task SpeakChapterAsync(int chapter, int session, CancellationTokenSource cancellation)
    {
        var token = cancellation.Token;
        using var player = new MediaPlayer();
        bool advance = false;
        try
        {
            var raw = await ReaderScriptAsync("chapterText", JsonValue.Create(chapter));
            if (JsonNode.Parse(raw) is not JsonArray { Count: > 0 } text) return;
            token.ThrowIfCancellationRequested();
            var first = await ReaderScriptAsync("firstVisible", JsonValue.Create(chapter));
            _ = int.TryParse(first, out var index);
            speechPlayer = player;
            var custom = SelectedHttpVoice();
            using var synthesizer = custom is null ? CreateSpeechSynthesizer() : null;
            for (; index < text.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                var paragraph = text[index]?.GetValue<string>() ?? "";
                if (paragraph == "" || paragraph.StartsWith("data:image/") || paragraph.StartsWith("@img:")) continue;
                await ReaderScriptAsync("highlight", JsonValue.Create(chapter), JsonValue.Create(index));
                await SpeakAsync(synthesizer, custom, player, paragraph, token);
            }
            advance = !token.IsCancellationRequested && session == readerSession &&
                store.Settings["tts"].B("autoNext", true) && chapter + 1 < readerChapters.Length;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            if (ReferenceEquals(speechPlayer, player)) speechPlayer = null;
            if (ReferenceEquals(speech, cancellation))
            {
                speech = null;
                readerSpeech!.Content = "朗读";
                if (readingToken != "") await ReaderScriptAsync("highlight", JsonValue.Create(chapter), JsonValue.Create(-1));
            }
            cancellation.Dispose();
        }
        if (advance && session == readerSession)
        {
            var next = readerChapters[chapter + 1];
            await ReadChapterAsync(chapter + 1);
            if (readingToken.Length > 0 && ReferenceEquals(readerChapters.ElementAtOrDefault(readingIndex), next))
                await ToggleSpeechAsync();
        }
    }
    private async Task SpeakAsync(SpeechSynthesizer? synthesizer, HttpVoice? voice, MediaPlayer player, string text, CancellationToken token)
    {
        if (voice is null)
        {
            using var stream = await synthesizer!.SynthesizeTextToStreamAsync(text).AsTask(token);
            await PlaySpeechAsync(player, stream, stream.ContentType, 1, token);
            return;
        }
        var preferences = store.Settings["tts"];
        var rate = Math.Clamp(preferences.D("rate", 1), .5, 2);
        var pitch = Math.Clamp(preferences.D("pitch", 1), .5, 2);
        for (int start = 0; start < text.Length;)
        {
            var length = Math.Min(voice.Method == "GET" ? 400 : 1600, text.Length - start);
            if (start + length < text.Length && char.IsHighSurrogate(text[start + length - 1])) length--;
            var (bytes, mime) = await voice.SynthesizeAsync(text.Substring(start, length), rate, pitch, token);
            using var audio = new MemoryStream(bytes, writable: false).AsRandomAccessStream();
            await PlaySpeechAsync(player, audio, mime, voice.HasRate ? 1 : rate, token);
            start += length;
        }
    }

    private static async Task PlaySpeechAsync(MediaPlayer player, IRandomAccessStream stream, string mime, double rate, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Finished(MediaPlayer sender, object args) => completion.TrySetResult();
        void Failed(MediaPlayer sender, MediaPlayerFailedEventArgs args) => completion.TrySetException(new IOException(args.ErrorMessage));
        player.MediaEnded += Finished;
        player.MediaFailed += Failed;
        try
        {
            using var source = MediaSource.CreateFromStream(stream, mime);
            player.Source = source;
            player.PlaybackSession.PlaybackRate = rate;
            player.Play();
            await completion.Task.WaitAsync(token);
        }
        finally
        {
            player.Pause();
            player.Source = null;
            player.MediaEnded -= Finished;
            player.MediaFailed -= Failed;
        }
    }
    private async Task PreviewSpeechAsync()
    {
        var custom = SelectedHttpVoice();
        using var synthesizer = custom is null ? CreateSpeechSynthesizer() : null;
        using var player = new MediaPlayer();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await SpeakAsync(synthesizer, custom, player, "这是一段Legado·UI3朗读试听。", timeout.Token);
    }
}
