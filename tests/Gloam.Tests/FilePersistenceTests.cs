using System;
using System.IO;
using Gloam.Core;
using Xunit;

namespace Gloam.Tests;

public sealed class FilePersistenceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "GloamFilePersistence", Guid.NewGuid().ToString("N"));
    public FilePersistenceTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void FailedReplacement_LeavesNoTemporaryFiles()
    {
        string path = Path.Combine(_directory, "destination.json");
        Directory.CreateDirectory(path);
        var failure = Record.Exception(() => SettingsPersistence.WriteAtomic(path, "{}"));
        Assert.True(failure is IOException or UnauthorizedAccessException);
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public void SavingSettings_DoesNotOverwriteExistingSidecar()
    {
        string path = Path.Combine(_directory, "profile.json");
        File.WriteAllText(path + ".tmp", "belongs to another operation");
        SettingsPersistence.WriteAtomic(path, "{}");
        Assert.Equal("belongs to another operation", File.ReadAllText(path + ".tmp"));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void BoundedRead_EnforcesDecodedCharacterLimit()
    {
        string path = Path.Combine(_directory, "characters.json");
        File.WriteAllText(path, new string('x', 101));
        Assert.Throws<InvalidDataException>(() => TextFileStore.ReadBounded(path, 1024, 100));
        Assert.Equal(new string('x', 101), TextFileStore.ReadBounded(path, 1024, 101));
    }

    [Fact]
    public void BoundedRead_PreservesUtf16BomSupport()
    {
        string path = Path.Combine(_directory, "unicode.json");
        File.WriteAllText(path, "Unicode report", System.Text.Encoding.Unicode);
        Assert.Equal("Unicode report", TextFileStore.ReadBounded(path, 1024, 100));
    }

    [Theory]
    [InlineData("123456", 100, 5)]
    [InlineData("\u03b1\u03b2\u03b3", 5, 100)]
    public void OversizedWrite_PreservesExistingFile(string text, long bytes, int characters)
    {
        string path = Path.Combine(_directory, "existing.json");
        File.WriteAllText(path, "original");
        Assert.Throws<InvalidDataException>(() => TextFileStore.WriteAtomic(path, text, bytes, characters));
        Assert.Equal("original", File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
