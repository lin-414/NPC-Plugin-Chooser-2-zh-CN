using System;
using System.Windows.Media;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace NPC_Plugin_Chooser_2.View_Models;

/// <summary>
/// One item in the Compare window. Wraps a snapshot of a gallery tile rather
/// than passing the tile VM itself so that (a) the compare packer sizes these
/// wrappers instead of clobbering the gallery tiles' ImageWidth/Height, and
/// (b) a LIVE source tile can get its own independent
/// <see cref="VM_InternalMugshotPreview"/> — the gallery viewport's VM cannot
/// be re-bound here, because one viewer VM can never serve two GLWpfControls
/// (each mints a private GL context; the VM tracks one set of GL object IDs).
/// The Compare launcher disposes each wrapper after the dialog closes, which
/// tears the detached viewport down through the view's GL-context-safe path.
/// </summary>
public sealed class VM_CompareMugshot : ReactiveObject, IHasMugshotImage, IDisposable
{
    /// <summary>Shown as the caption; the compare template binds it by name.</summary>
    public string ModName { get; }

    [Reactive] public ImageSource? MugshotSource { get; set; }
    [Reactive] public double ImageWidth { get; set; }
    [Reactive] public double ImageHeight { get; set; }
    public int OriginalPixelWidth { get; set; }
    public int OriginalPixelHeight { get; set; }
    public double OriginalDipWidth { get; set; }
    public double OriginalDipHeight { get; set; }
    public double OriginalDipDiagonal { get; set; }
    public bool HasMugshot { get; }
    public bool IsVisible => true;
    public string ImagePath { get; set; }

    /// <summary>True when this compare item renders as an embedded viewport
    /// (its source tile was live when Compare was clicked).</summary>
    public bool IsLiveTile { get; }

    /// <summary>The item's own detached preview VM; null for static items.</summary>
    public VM_InternalMugshotPreview? LiveTilePreview { get; }

    public VM_CompareMugshot(VM_NpcsMenuMugshot source)
    {
        ModName = source.ModName;
        MugshotSource = source.MugshotSource;
        ImagePath = source.ImagePath;
        // For a live source tile these getters already report the mugshot
        // OUTPUT dimensions, which is exactly the aspect the viewport needs.
        OriginalPixelWidth = source.OriginalPixelWidth;
        OriginalPixelHeight = source.OriginalPixelHeight;
        OriginalDipWidth = source.OriginalDipWidth;
        OriginalDipHeight = source.OriginalDipHeight;
        OriginalDipDiagonal = source.OriginalDipDiagonal;

        if (source.IsLiveTile)
        {
            LiveTilePreview = source.CreateDetachedLivePreview();
            IsLiveTile = LiveTilePreview != null;
        }

        // The compare window's item filter and packer both require a non-empty
        // ImagePath; a live tile that never displayed an image has none, so
        // give it a sentinel (nothing dereferences the path — the packer only
        // checks non-emptiness, and display comes from the viewport).
        if (IsLiveTile && string.IsNullOrEmpty(ImagePath))
        {
            ImagePath = "live-tile";
        }
        HasMugshot = source.HasMugshot || IsLiveTile;
    }

    public void Dispose()
    {
        var preview = LiveTilePreview;
        if (preview != null && !preview.RequestViewShutdown())
        {
            preview.Dispose();
        }
    }
}
