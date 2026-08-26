using System;
using System.IO;

namespace deltanium_store.Tests.Helpers
{
    /// <summary>
    /// Helper class for managing temporary files and directories in tests
    /// </summary>
    public class TestFileHelper : IDisposable
    {
        private readonly string _tempDirectory;

        public TestFileHelper()
        {
            _tempDirectory = Path.Combine(Path.GetTempPath(), "deltanium-store-tests", Guid.NewGuid().ToString());
            Directory.CreateDirectory(_tempDirectory);
        }

        public string TempDirectory => _tempDirectory;

        public string CreateSubdirectory(string name)
        {
            var path = Path.Combine(_tempDirectory, name);
            Directory.CreateDirectory(path);
            return path;
        }

        public string CreateTestFile(string fileName, string content = "")
        {
            var filePath = Path.Combine(_tempDirectory, fileName);
            File.WriteAllText(filePath, content);
            return filePath;
        }

        public string CreateTestFile(string fileName, byte[] content)
        {
            var filePath = Path.Combine(_tempDirectory, fileName);
            File.WriteAllBytes(filePath, content);
            return filePath;
        }

        public void Dispose()
        {
            if (Directory.Exists(_tempDirectory))
            {
                try
                {
                    Directory.Delete(_tempDirectory, recursive: true);
                }
                catch
                {
                    // Ignore cleanup errors
                }
            }
        }
    }
}








