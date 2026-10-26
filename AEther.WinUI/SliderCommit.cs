using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace AEther.WinUI;

/// <summary>Preview pointer edits locally; publish the final value on release.
/// Keyboard and automation edits publish immediately.</summary>
internal static class SliderCommit
{
    public static void Bind(Slider slider, Action<double> preview, Action<double> commit)
    {
        bool editing = false;
        double published = slider.Value;
        void Publish()
        {
            if (slider.Value == published) return;
            published = slider.Value;
            commit(published);
        }
        void Finish(object sender, PointerRoutedEventArgs args)
        {
            if (!editing) return;
            editing = false;
            Publish();
        }
        // Slider/Thumb handles these events internally, so observe handled events too.
        slider.AddHandler(UIElement.PointerPressedEvent,
            new PointerEventHandler((_, _) => editing = true), true);
        slider.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(Finish), true);
        slider.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(Finish), true);
        slider.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(Finish), true);
        slider.ValueChanged += (_, e) =>
        {
            preview(e.NewValue);
            if (!editing) Publish();
        };
    }
}
