using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GuiPhoneBrowser.Models;

namespace GuiPhoneBrowser.Services
{
    public class LocalFileService
    {
        public class PathShortcut
        {
            public string Name { get; set; } = string.Empty;
            public string Path { get; set; } = string.Empty;
            public override string ToString() => Name;
        }

        public List<PathShortcut> GetShortcuts()
        {
            var shortcuts = new List<PathShortcut>();

            try
            {
                foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady))
                {
                    string label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? "Local Disk" : drive.VolumeLabel;
                    shortcuts.Add(new PathShortcut
                    {
                        Name = $"{drive.Name.TrimEnd('\\')} ({label})",
                        Path = drive.RootDirectory.FullName
                    });
                }
            }
            catch { }

            void AddSpecial(Environment.SpecialFolder folder, string name)
            {
                try
                {
                    string p = Environment.GetFolderPath(folder);
                    if (!string.IsNullOrEmpty(p) && Directory.Exists(p))
                    {
                        shortcuts.Add(new PathShortcut { Name = name, Path = p });
                    }
                }
                catch { }
            }

            AddSpecial(Environment.SpecialFolder.UserProfile, "Home");
            AddSpecial(Environment.SpecialFolder.Desktop, "Desktop");
            AddSpecial(Environment.SpecialFolder.MyDocuments, "Documents");
            
            try
            {
                string downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                if (Directory.Exists(downloads))
                {
                    shortcuts.Add(new PathShortcut { Name = "Downloads", Path = downloads });
                }
            }
            catch { }

            AddSpecial(Environment.SpecialFolder.MyPictures, "Pictures");
            AddSpecial(Environment.SpecialFolder.MyMusic, "Music");
            AddSpecial(Environment.SpecialFolder.MyVideos, "Videos");

            return shortcuts;
        }

        public Task<List<FileItem>> GetDirectoryItemsAsync(string path)
        {
            return Task.Run(() =>
            {
                var list = new List<FileItem>();
                if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
                {
                    return list;
                }

                var dirInfo = new DirectoryInfo(path);

                // Add parent directory item if not root
                if (dirInfo.Parent != null)
                {
                    list.Add(new FileItem
                    {
                        Name = "..",
                        FullPath = dirInfo.Parent.FullName,
                        IsDirectory = true,
                        IsParentDirectory = true,
                        Size = 0,
                        ModifiedTime = null
                    });
                }

                try
                {
                    var dirs = dirInfo.EnumerateDirectories();
                    foreach (var d in dirs.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        // Skip hidden or system folders if desired, or keep standard
                        bool isHidden = (d.Attributes & FileAttributes.Hidden) != 0;
                        if (isHidden && d.Name.StartsWith("$")) continue;

                        list.Add(new FileItem
                        {
                            Name = d.Name,
                            FullPath = d.FullName,
                            IsDirectory = true,
                            Size = 0,
                            ModifiedTime = d.LastWriteTime
                        });
                    }
                }
                catch (UnauthorizedAccessException) { }
                catch (Exception) { }

                try
                {
                    var files = dirInfo.EnumerateFiles();
                    foreach (var f in files.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        list.Add(new FileItem
                        {
                            Name = f.Name,
                            FullPath = f.FullName,
                            IsDirectory = false,
                            Size = f.Length,
                            IsSizeKnown = true,
                            ModifiedTime = f.LastWriteTime
                        });
                    }
                }
                catch (UnauthorizedAccessException) { }
                catch (Exception) { }

                return list;
            });
        }

        public bool CreateDirectory(string path)
        {
            try
            {
                Directory.CreateDirectory(path);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public bool DeleteItem(FileItem item)
        {
            try
            {
                if (item.IsDirectory)
                {
                    Directory.Delete(item.FullPath, recursive: true);
                }
                else
                {
                    File.Delete(item.FullPath);
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        public bool SetFileTimestamp(string filePath, DateTime lastWriteTimeUtc)
        {
            try
            {
                if (File.Exists(filePath))
                {
                    File.SetLastWriteTimeUtc(filePath, lastWriteTimeUtc);
                    return true;
                }
                if (Directory.Exists(filePath))
                {
                    Directory.SetLastWriteTimeUtc(filePath, lastWriteTimeUtc);
                    return true;
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        public long GetFileSize(string filePath)
        {
            try
            {
                return new FileInfo(filePath).Length;
            }
            catch
            {
                return -1;
            }
        }
    }
}
