using Avalonia;

namespace Skua.Avalonia.Tests;

/// <summary>Each Mac App's main window opens down and right of those of the other apps open on its data folder.</summary>
public sealed class WindowCascadeTests
{
    private static readonly PixelRect Screen = new(0, 25, 1512, 920);
    private static readonly PixelSize Size = new(958, 706);
    private static readonly PixelPoint Origin = new(277, 82);

    [Fact]
    public void Apps_open_on_one_data_folder_take_the_lowest_free_slot_and_a_quit_apps_slot_is_taken_again()
    {
        string skuaDir = Directory.CreateTempSubdirectory("skua-cascade-").FullName;
        try
        {
            using WindowCascade first = WindowCascade.Claim(skuaDir);
            WindowCascade second = WindowCascade.Claim(skuaDir);
            using WindowCascade third = WindowCascade.Claim(skuaDir);
            Assert.Equal([0, 1, 2], [first.Slot, second.Slot, third.Slot]);

            second.Dispose();
            using WindowCascade next = WindowCascade.Claim(skuaDir);
            Assert.Equal(1, next.Slot);
        }
        finally
        {
            Directory.Delete(skuaDir, recursive: true);
        }
    }

    [Fact]
    public void Each_slot_opens_a_step_further_down_and_right()
    {
        Assert.Equal(Origin, WindowCascade.Offset(Origin, Size, Screen, 0, 1));
        Assert.Equal(new PixelPoint(277 + 28, 82 + 28), WindowCascade.Offset(Origin, Size, Screen, 1, 1));
        Assert.Equal(new PixelPoint(277 + 56, 82 + 56), WindowCascade.Offset(Origin, Size, Screen, 2, 1));
        // A step is in points, so twice the pixels on a Retina display.
        Assert.Equal(new PixelPoint(554 + 56, 164 + 56), WindowCascade.Offset(new PixelPoint(554, 164), new PixelSize(1916, 1412), new PixelRect(0, 50, 3024, 1840), 1, 2));
    }

    [Fact]
    public void Once_the_next_step_would_leave_the_screen_the_slots_start_over_from_the_first_place()
    {
        // The window's bottom has 945 - 82 - 706 = 157 points to go: five steps fit, so six places.
        Assert.Equal(new PixelPoint(277 + 5 * 28, 82 + 5 * 28), WindowCascade.Offset(Origin, Size, Screen, 5, 1));
        Assert.Equal(Origin, WindowCascade.Offset(Origin, Size, Screen, 6, 1));
        Assert.Equal(new PixelPoint(277 + 28, 82 + 28), WindowCascade.Offset(Origin, Size, Screen, 7, 1));
        // A window as big as the screen stays where it opens.
        Assert.Equal(PixelPoint.Origin, WindowCascade.Offset(PixelPoint.Origin, new PixelSize(1512, 945), new PixelRect(0, 0, 1512, 945), 3, 1));
    }
}
