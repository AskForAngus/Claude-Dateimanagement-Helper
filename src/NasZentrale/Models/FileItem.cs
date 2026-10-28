using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using NasZentrale.Core.TrueNas;
using Wpf.Ui.Controls;

namespace NasZentrale.Models;

/// <summary>Eine Zeile in der Dateiliste.</summary>
public sealed partial class FileItem : ObservableObject
{
    public FileItem(FsEntry entry) => Entry = entry;

    public FsEntry Entry { get; }
    public string Name => Entry.Name;
    public string Path => Entry.Path;
    public bool IsDirectory => Entry.IsDirectory;
    public bool IsDataset => Entry.IsMountpoint;

    [ObservableProperty] private DateTime? _modified;
    [ObservableProperty] private bool _hasOwnRights;

    public string ModifiedText => Modified?.ToString("dd.MM.yyyy HH:mm") ?? "";

    partial void OnModifiedChanged(DateTime? value) => OnPropertyChanged(nameof(ModifiedText));

    public string SizeText => IsDirectory ? "" : FormatSize(Entry.Size);

    public long SortSize => IsDirectory ? -1 : Entry.Size;

    public string TypeText => IsDataset ? "Dataset" : IsDirectory ? "Ordner" : Extension.Length > 0 ? $"{Extension.ToUpperInvariant()}-Datei" : "Datei";

    private string Extension => IsDirectory ? "" : System.IO.Path.GetExtension(Name).TrimStart('.');

    public SymbolRegular Icon
    {
        get
        {
            if (IsDataset) return SymbolRegular.Database24;
            if (IsDirectory) return SymbolRegular.Folder24;
            return Extension.ToLowerInvariant() switch
            {
                "pdf" => SymbolRegular.DocumentPdf24,
                "xls" or "xlsx" or "xlsm" or "csv" or "ods" => SymbolRegular.DocumentTable24,
                "doc" or "docx" or "txt" or "rtf" or "odt" or "md" => SymbolRegular.DocumentText24,
                "ppt" or "pptx" or "odp" => SymbolRegular.SlideText24,
                "zip" or "7z" or "rar" or "gz" or "tar" => SymbolRegular.FolderZip24,
                "jpg" or "jpeg" or "png" or "gif" or "bmp" or "tif" or "tiff" or "heic" or "webp" or "svg" => SymbolRegular.Image24,
                "mp4" or "mov" or "avi" or "mkv" or "wmv" => SymbolRegular.Video24,
                "mp3" or "wav" or "flac" or "m4a" => SymbolRegular.MusicNote224,
                "msg" or "eml" => SymbolRegular.Mail24,
                _ => SymbolRegular.Document24,
            };
        }
    }

    public static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes;
        var u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{bytes} B" : $"{v:0.#} {units[u]}";
    }
}
