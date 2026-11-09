using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace DownloadDock.Integration.Tests;

public class SerializationTests
{
    [Fact]
    public void Request_round_trips_with_camel_case_and_no_nulls()
    {
        var msg = IntegrationMessage.Request("7", IntegrationCommands.OpenFolder, new OpenFolderRequest { Path = @"C:\Users\Ana\Documents", NewWindow = true });
        var json = Encoding.UTF8.GetString(IntegrationJson.Serialize(msg));
        Assert.Contains("\"type\":\"request\"", json);
        Assert.Contains("\"command\":\"finder.openFolder\"", json);
        Assert.Contains("\"newWindow\":true", json);
        Assert.DoesNotContain("\"result\"", json);
        Assert.DoesNotContain("null", json);

        var back = IntegrationJson.Deserialize(Encoding.UTF8.GetBytes(json));
        Assert.Equal("7", back.Id);
        var payload = IntegrationJson.FromElement<OpenFolderRequest>(back.Payload)!;
        Assert.Equal(@"C:\Users\Ana\Documents", payload.Path);
        Assert.True(payload.NewWindow);
    }

    [Fact]
    public void Error_and_event_messages_round_trip()
    {
        var err = IntegrationJson.Deserialize(IntegrationJson.Serialize(IntegrationMessage.Failure("1", IntegrationErrorCodes.InvalidPath, "nope")));
        Assert.False(err.Ok);
        Assert.Equal(IntegrationErrorCodes.InvalidPath, err.Error!.Code);

        var ev = IntegrationJson.Deserialize(IntegrationJson.Serialize(IntegrationMessage.EventMessage(IntegrationEvents.ScreenshotCaptured, 42,
            new FileEventPayload { Path = @"C:\Pics\Screenshot 2026-11-09 08-30-15.png", Name = "Screenshot 2026-11-09 08-30-15.png", Source = "region" })));
        Assert.Equal(MessageTypes.Event, ev.Type);
        Assert.Equal(42, ev.Seq);
        Assert.Equal("region", IntegrationJson.FromElement<FileEventPayload>(ev.Payload)!.Source);
    }

    [Fact]
    public void Unicode_paths_survive()
    {
        var path = @"\\nas\共有\Fotos – Ünïcødé\😀 résumé.pdf";
        var back = IntegrationJson.Deserialize(IntegrationJson.Serialize(IntegrationMessage.Request("1", IntegrationCommands.RevealFile, new RevealFileRequest { Path = path })));
        Assert.Equal(path, IntegrationJson.FromElement<RevealFileRequest>(back.Payload)!.Path);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"type\":")]
    public void Malformed_messages_are_rejected_with_invalid_message(string text)
    {
        var ex = Assert.Throws<IntegrationException>(() => IntegrationJson.Deserialize(Encoding.UTF8.GetBytes(text)));
        Assert.Equal(IntegrationErrorCodes.InvalidMessage, ex.Code);
    }

    [Fact]
    public void Deeply_nested_json_is_refused()
    {
        var deep = "{\"type\":\"request\",\"payload\":" + new string('[', 100) + new string(']', 100) + "}";
        Assert.Throws<IntegrationException>(() => IntegrationJson.Deserialize(Encoding.UTF8.GetBytes(deep)));
    }
}

public class FramingTests
{
    [Fact]
    public async Task Frames_round_trip()
    {
        var ms = new MemoryStream();
        await MessageFraming.WriteAsync(ms, IntegrationMessage.Request("1", IntegrationCommands.Ping, null), 1024, default);
        await MessageFraming.WriteAsync(ms, IntegrationMessage.Success("1", new PingResult()), 1024, default);
        ms.Position = 0;
        Assert.Equal(IntegrationCommands.Ping, (await MessageFraming.ReadAsync(ms, 1024, default))!.Command);
        Assert.True((await MessageFraming.ReadAsync(ms, 1024, default))!.Ok);
        Assert.Null(await MessageFraming.ReadAsync(ms, 1024, default)); // clean end
    }

    [Fact]
    public async Task Oversized_outgoing_message_is_refused()
    {
        var big = new FilesReceivedRequest { Files = Enumerable.Range(0, 2000).Select(i => new ReceivedFile { Path = $@"C:\x\{i}.png" }).ToList() };
        var ex = await Assert.ThrowsAsync<IntegrationException>(() => MessageFraming.WriteAsync(new MemoryStream(), IntegrationMessage.Request("1", "x", big), 4096, default));
        Assert.Equal(IntegrationErrorCodes.MessageTooLarge, ex.Code);
    }

    [Fact]
    public async Task Oversized_incoming_length_is_rejected_before_reading_the_body()
    {
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 500_000_000); // would be a 500 MB allocation
        var ex = await Assert.ThrowsAsync<IntegrationException>(() => MessageFraming.ReadAsync(new MemoryStream(header), 64 * 1024, default));
        Assert.Equal(IntegrationErrorCodes.MessageTooLarge, ex.Code);
    }

    [Fact]
    public async Task Truncated_and_empty_frames_are_errors()
    {
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 50);
        var truncated = new MemoryStream(header.Concat(new byte[10]).ToArray());
        Assert.Equal(IntegrationErrorCodes.Disconnected, (await Assert.ThrowsAsync<IntegrationException>(() => MessageFraming.ReadAsync(truncated, 1024, default))).Code);
        Assert.Equal(IntegrationErrorCodes.InvalidMessage, (await Assert.ThrowsAsync<IntegrationException>(() => MessageFraming.ReadAsync(new MemoryStream(new byte[4]), 1024, default))).Code);
    }
}

public class PathRulesTests
{
    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"C:\Users\Ana\Downloads")]
    [InlineData(@"C:\Users\Ana\Downloads\")]
    [InlineData(@"Z:\Mapped drive\Project Brief.pdf")]
    [InlineData(@"G:\My Drive\Shared with me\notes.docx")]
    [InlineData(@"\\fileserver\share")]
    [InlineData(@"\\fileserver\share\Folder With Spaces\file (2).png")]
    [InlineData(@"\\?\C:\very\long\path\file.txt")]
    [InlineData(@"\\?\UNC\fileserver\share\file.txt")]
    [InlineData(@"C:\Users\Ana\Résumé – 日本語\😀.pdf")]
    [InlineData("C:/Users/Ana/Downloads")]
    public void Accepts_absolute_windows_paths(string path) => Assert.Null(PathRules.Validate(path));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Downloads")]
    [InlineData(@"..\secret")]
    [InlineData(@"C:relative\path")]
    [InlineData(@"\Users\Ana")]
    [InlineData(@"C:\Users\..\Windows")]
    [InlineData(@"C:\Users\.\Ana")]
    [InlineData(@"C:\Users\Ana\*.pdf")]
    [InlineData(@"C:\Users\Ana\file?.pdf")]
    [InlineData(@"C:\Users\Ana\a""b")]
    [InlineData(@"C:\Users\Ana\file.txt:secret")]
    [InlineData(@"\\.\PhysicalDrive0")]
    [InlineData(@"\\?\GLOBALROOT\Device\HarddiskVolume1")]
    [InlineData(@"\\server")]
    [InlineData(@"\\server\")]
    [InlineData(@"C:\Users\\Ana")]
    [InlineData(@"C:\Users\Ana ")]
    [InlineData(" C:\\Users")]
    [InlineData("C:\\Users\\Ana\u0001")]
    [InlineData(@"C:\folder.\file")]
    public void Rejects_unsafe_or_relative_paths(string? path) => Assert.NotNull(PathRules.Validate(path));

    [Fact]
    public void Rejects_overlong_paths()
        => Assert.NotNull(PathRules.Validate(@"C:\" + string.Join('\\', Enumerable.Repeat(new string('a', 200), 200))));

    [Theory]
    [InlineData(@"C:\Users\Ana\file.pdf", @"C:\Users\Ana")]
    [InlineData(@"C:\file.pdf", @"C:\")]
    [InlineData(@"\\server\share\dir\file.pdf", @"\\server\share\dir")]
    public void Parent_of(string path, string parent) => Assert.Equal(parent, PathRules.ParentOf(path));
}

public class VersionTests
{
    [Theory]
    [InlineData("1.0", "1.0")]
    [InlineData("1.7", "1.0")]
    [InlineData("1", "1.0")]
    public void Same_major_negotiates_lower_minor(string peer, string expected)
        => Assert.Equal(expected, VersionNegotiation.Negotiate(peer, 1, 0));

    [Fact]
    public void Lower_minor_wins_on_either_side() => Assert.Equal("1.2", VersionNegotiation.Negotiate("1.2", 1, 5));

    [Theory]
    [InlineData("2.0")]
    [InlineData("0.9")]
    [InlineData("banana")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("1.0.0")]
    [InlineData("-1.0")]
    public void Different_major_or_garbage_is_incompatible(string? peer) => Assert.Null(VersionNegotiation.Negotiate(peer, 1, 0));

    [Fact]
    public void Pipe_names_are_versioned_and_session_scoped()
    {
        var dd = IntegrationEndpoints.DownloadDockPipeName();
        var finder = IntegrationEndpoints.FinderPipeName();
        Assert.StartsWith("DownloadDock.Integration.v1.", dd);
        Assert.StartsWith("FinderForWindows.Integration.v1.", finder);
        Assert.Equal(IntegrationEndpoints.SessionKey(), dd.Split('.').Last());
        Assert.Equal(16, IntegrationEndpoints.SessionKey().Length);
        Assert.NotEqual(dd, finder);
    }
}
