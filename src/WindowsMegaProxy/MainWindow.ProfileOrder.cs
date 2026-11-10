using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace WindowsMegaProxy;

public partial class MainWindow
{
    private Point profileDragStart;
    private string? draggedProfile;
    private ListBoxItem? profileDropTarget;
    private bool savingProfileOrder;
    private long lastDragScroll;
    private const string ProfileDragFormat = "WindowsMegaProxy.ProfileOrder";

    private void ProfileDragDown(object sender, MouseButtonEventArgs e)
    {
        draggedProfile = null;
        if (!canEdit || savingProfileOrder || e.OriginalSource is not DependencyObject origin) return;
        if (ItemsControl.ContainerFromElement(ProfilesList, origin) is not ListBoxItem { DataContext: ProxyProfile profile }) return;
        profileDragStart = e.GetPosition(ProfilesList);
        draggedProfile = profile.Id;
    }

    private void ProfileDragMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) { draggedProfile = null; return; }
        if (draggedProfile is null || !canEdit || savingProfileOrder) return;
        var point = e.GetPosition(ProfilesList);
        if (Math.Abs(point.X - profileDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(point.Y - profileDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        try { DragDrop.DoDragDrop(ProfilesList, new DataObject(ProfileDragFormat, draggedProfile), DragDropEffects.Move); }
        finally { draggedProfile = null; ClearProfileDropTarget(); }
        e.Handled = true;
    }

    private bool IsProfileDrag(DragEventArgs e) => canEdit && !quitting && !settingsLoading && !installingAppUpdate && !savingProfileOrder &&
        draggedProfile is not null && e.Data.GetDataPresent(ProfileDragFormat) && e.Data.GetData(ProfileDragFormat) as string == draggedProfile;

    private void ClearProfileDropTarget()
    {
        profileDropTarget?.ClearValue(Control.BorderBrushProperty);
        profileDropTarget?.ClearValue(Control.BorderThicknessProperty);
        profileDropTarget = null;
    }

    private void ProfileDragLeave(object sender, DragEventArgs e) => ClearProfileDropTarget();

    private void ProfileDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = IsProfileDrag(e) ? DragDropEffects.Move : DragDropEffects.None;
        ClearProfileDropTarget();
        if (e.Effects == DragDropEffects.None) return;
        if (e.OriginalSource is DependencyObject origin && ItemsControl.ContainerFromElement(ProfilesList, origin) is ListBoxItem item)
        {
            profileDropTarget = item;
            item.SetResourceReference(Control.BorderBrushProperty, "AccentTextFillColorPrimaryBrush");
            item.BorderThickness = e.GetPosition(item).Y < item.ActualHeight / 2 ? new Thickness(0, 2, 0, 0) : new Thickness(0, 0, 0, 2);
        }
        if (Environment.TickCount64 - lastDragScroll < 100) return;
        var scroll = Descendants<ScrollViewer>(ProfilesList).FirstOrDefault();
        double y = e.GetPosition(ProfilesList).Y;
        if (y < 28) scroll?.LineUp();
        else if (y > ProfilesList.ActualHeight - 28) scroll?.LineDown();
        lastDragScroll = Environment.TickCount64;
    }

    private async void ProfileDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (!IsProfileDrag(e)) { e.Effects = DragDropEffects.None; return; }
        var item = e.OriginalSource is DependencyObject origin ? ItemsControl.ContainerFromElement(ProfilesList, origin) as ListBoxItem : null;
        string? target = (item?.DataContext as ProxyProfile)?.Id;
        bool after = item is null || e.GetPosition(item).Y >= item.ActualHeight / 2;
        ClearProfileDropTarget();
        try { await MoveProfileAsync(draggedProfile!, target, after); }
        catch (Exception error) { ShowError(error); }
    }

    private async Task MoveProfileAsync(string id, string? targetId, bool after)
    {
        if (!canEdit || quitting || settingsLoading || installingAppUpdate || savingProfileOrder || id == targetId) return;
        var updated = CaptureSettings();
        var profile = updated.Profiles.FirstOrDefault(item => item.Id == id);
        if (profile is null || targetId is not null && !updated.Profiles.Any(item => item.Id == targetId)) return;
        int previous = updated.Profiles.IndexOf(profile);
        updated.Profiles.Remove(profile);
        int index = targetId is null ? updated.Profiles.Count : updated.Profiles.FindIndex(item => item.Id == targetId) + (after ? 1 : 0);
        if (index == previous) return;
        updated.Profiles.Insert(index, profile);
        savingProfileOrder = true;
        try
        {
            await CommitSettingsAsync(updated);
            ProfilesList.SelectedItem = settings.Profiles.FirstOrDefault(item => item.Id == id);
        }
        finally { savingProfileOrder = false; }
    }
}
