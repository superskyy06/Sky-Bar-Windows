using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows.Media;

namespace SkyBar.CoreServices;

public class ShelfFileItem
{
    public string FullPath { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public string FileSizeText { get; init; } = string.Empty;
    public ImageSource? FileIcon { get; init; }
}

public sealed class FileShelfService
{
    public ObservableCollection<ShelfFileItem> ShelfItems { get; } = new();

    public void AddFilesToShelf(string[] filePaths)
    {
        foreach (string path in filePaths)
        {
            if (!File.Exists(path) && !Directory.Exists(path)) continue;
            if (ShelfItems.Any(f => f.FullPath.Equals(path, StringComparison.OrdinalIgnoreCase))) continue;
            while (ShelfItems.Count >= 12) ShelfItems.RemoveAt(ShelfItems.Count - 1);

            string sizeText = "Folder";
            if (File.Exists(path))
            {
                try
                {
                    long len = new FileInfo(path).Length;
                    sizeText = len >= 1024 * 1024
                        ? $"{len / (1024.0 * 1024.0):0.1} MB"
                        : $"{Math.Max(1, len / 1024.0):0} KB";
                }
                catch { sizeText = "File"; }
            }

            ShelfItems.Insert(0, new ShelfFileItem
            {
                FullPath = path,
                FileName = Path.GetFileName(path),
                FileSizeText = sizeText
            });
        }
    }

    public void RemoveFromShelf(ShelfFileItem item) => ShelfItems.Remove(item);

    public void ClearShelf() => ShelfItems.Clear();
}
