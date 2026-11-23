using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace InkCanvas.Controls.Lifecycle;

/// <summary>Coalesces responsive updates after the current XAML layout pass.</summary>
public static class DeferredLayout
{
    public static void Observe(FrameworkElement element, Action update)
    {
        bool queued = false;
        void Request()
        {
            if (queued) return;
            queued = true;
            if (!element.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                queued = false;
                if (element.IsLoaded) update();
            })) queued = false;
        }
        element.Loaded += (_, _) => Request();
        element.SizeChanged += (_, _) => Request();
    }
}
