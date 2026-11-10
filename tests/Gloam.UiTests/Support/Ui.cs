using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace Gloam.UiTests.Support;

/// <summary>UI Automation helpers that wait for what they look for.</summary>
internal static class Ui
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    public static T WaitFor<T>(Func<T?> probe, string what, TimeSpan? timeout = null) where T : class
    {
        var deadline = DateTime.UtcNow + (timeout ?? DefaultTimeout);
        Exception? last = null;
        while (true)
        {
            try
            {
                if (probe() is { } found) return found;
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException)
            {
                last = ex; // the tree changed under us: look again
            }
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Timed out waiting for {what}", last);
            Thread.Sleep(100);
        }
    }

    public static void Until(Func<bool> condition, string what, TimeSpan? timeout = null) =>
        WaitFor(() => condition() ? "" : null, what, timeout);

    public static AutomationElement ById(this AutomationElement root, string id, TimeSpan? timeout = null) =>
        WaitFor(() => root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, id)),
            $"element #{id}", timeout);

    public static string Text(this AutomationElement element) => element.Current.Name;

    /// <summary>Waits until the element's text is <paramref name="expected"/>.</summary>
    public static void HasText(this AutomationElement root, string id, string expected, TimeSpan? timeout = null)
    {
        string? last = null;
        try
        {
            Until(() => (last = root.ById(id).Text()) == expected, $"#{id} to read '{expected}'", timeout);
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException($"#{id} reads '{last}', not '{expected}'", ex);
        }
    }

    public static void Invoke(this AutomationElement element) =>
        ((InvokePattern)element.GetCurrentPattern(InvokePattern.Pattern)).Invoke();

    public static bool IsChecked(this AutomationElement element) =>
        ((TogglePattern)element.GetCurrentPattern(TogglePattern.Pattern)).Current.ToggleState == ToggleState.On;

    public static void SetChecked(this AutomationElement element, bool on)
    {
        if (element.IsChecked() != on) ((TogglePattern)element.GetCurrentPattern(TogglePattern.Pattern)).Toggle();
        Until(() => element.IsChecked() == on, $"'{element.Text()}' to be {(on ? "checked" : "unchecked")}");
    }

    /// <summary>A radio button (the big mode buttons): chosen or not.</summary>
    public static bool IsSelected(this AutomationElement element) =>
        ((SelectionItemPattern)element.GetCurrentPattern(SelectionItemPattern.Pattern)).Current.IsSelected;

    public static void Select(this AutomationElement element) =>
        ((SelectionItemPattern)element.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();

    /// <summary>Moves a slider, as a drag would end.</summary>
    public static void SetRange(this AutomationElement element, double value) =>
        ((RangeValuePattern)element.GetCurrentPattern(RangeValuePattern.Pattern)).SetValue(value);

    public static double Range(this AutomationElement element) =>
        ((RangeValuePattern)element.GetCurrentPattern(RangeValuePattern.Pattern)).Current.Value;

    /// <summary>Picks a combo box item by its text.</summary>
    public static void Choose(this AutomationElement comboBox, string item)
    {
        var expand = (ExpandCollapsePattern)comboBox.GetCurrentPattern(ExpandCollapsePattern.Pattern);
        expand.Expand();
        try
        {
            WaitFor(() => comboBox.FindFirst(TreeScope.Descendants, new AndCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
                    new PropertyCondition(AutomationElement.NameProperty, item))),
                $"item '{item}'").Select();
        }
        finally
        {
            expand.Collapse();
        }
    }

    public static string Selection(this AutomationElement comboBox) =>
        ((SelectionPattern)comboBox.GetCurrentPattern(SelectionPattern.Pattern)).Current.GetSelection()
            .FirstOrDefault()?.Current.Name ?? "";
}
