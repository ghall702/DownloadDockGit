using DownloadDock.Core.Downloads;

namespace DownloadDock.Core.Tests;

public class DownloadFileFilterTests
{
    [Theory]
    [InlineData("report.pdf.crdownload")]
    [InlineData("Unconfirmed 123456.crdownload")]
    [InlineData("movie.mkv.part")]
    [InlineData("setup.exe.partial")]
    [InlineData("photo.jpg.download")]
    [InlineData("file.opdownload")]
    [InlineData("~$Report.docx")]
    [InlineData("~WRL0003.tmp")]
    [InlineData("download.tmp")]
    [InlineData("desktop.ini")]
    [InlineData("Thumbs.db")]
    [InlineData("big.iso.!ut")]
    [InlineData("archive.zip.aria2")]
    [InlineData("")]
    public void Partial_and_system_files_are_hidden(string name)
        => Assert.True(DownloadFileFilter.IsTemporaryOrPartial(name));

    [Theory]
    [InlineData("Project Brief.pdf")]
    [InlineData("Beach Photo.jpg")]
    [InlineData("Design Assets.zip")]
    [InlineData("Presentation.pptx")]
    [InlineData("partial-results.csv")]
    [InlineData("crdownload-notes.txt")]
    [InlineData("README")]
    public void Completed_downloads_are_shown(string name)
        => Assert.False(DownloadFileFilter.IsTemporaryOrPartial(name));

    [Fact]
    public void Hidden_and_system_attributes_are_hidden()
    {
        Assert.False(DownloadFileFilter.ShouldShow("a.pdf", FileAttributes.Hidden));
        Assert.False(DownloadFileFilter.ShouldShow("a.pdf", FileAttributes.System | FileAttributes.Archive));
        Assert.True(DownloadFileFilter.ShouldShow("a.pdf", FileAttributes.Archive));
        Assert.True(DownloadFileFilter.ShouldShow("a.pdf", FileAttributes.Normal));
    }
}
