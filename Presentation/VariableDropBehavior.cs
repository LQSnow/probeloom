using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Input;
using ProbeLoom.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.System;
using Windows.UI.Core;

namespace ProbeLoom.Presentation;

public static class VariableDropBehavior
{
    public const string DataFormat = "ProbeLoom.VariableReference";

    private static readonly SoftwareBitmap TransparentDragBitmap = new(
        BitmapPixelFormat.Bgra8,
        1,
        1,
        BitmapAlphaMode.Premultiplied);

    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(VariableDropBehavior),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject target) =>
        (bool)target.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject target, bool value) =>
        target.SetValue(IsEnabledProperty, value);

    public static void SetDragData(DataPackage data, string variableName)
    {
        var reference = VariableReference.Format(variableName);
        if (reference.Length == 0)
        {
            return;
        }

        data.SetData(DataFormat, variableName.Trim());
        data.SetText(reference);
        data.RequestedOperation = DataPackageOperation.Copy;
    }

    public static void HideDragVisual(DragUI dragUI) =>
        dragUI.SetContentFromSoftwareBitmap(TransparentDragBitmap);

    private static void OnIsEnabledChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is not TextBox textBox)
        {
            return;
        }

        textBox.AllowDrop = args.NewValue is true;
        textBox.DragOver -= TextBox_DragOver;
        textBox.Drop -= TextBox_Drop;
        if (args.NewValue is true)
        {
            textBox.DragOver += TextBox_DragOver;
            textBox.Drop += TextBox_Drop;
            ToolTipService.SetToolTip(
                textBox,
                "可输入 {{variable.name}}，或从 Inspector 拖入变量。");
        }
        else
        {
            ToolTipService.SetToolTip(textBox, null);
        }
    }

    private static void TextBox_DragOver(object sender, DragEventArgs e)
    {
        if (sender is TextBox textBox && e.DataView.Contains(DataFormat))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            MoveInsertionCaret(textBox, e.GetPosition(textBox));
            e.DragUIOverride.Clear();
            e.DragUIOverride.IsContentVisible = false;
            e.DragUIOverride.IsGlyphVisible = false;
            e.DragUIOverride.IsCaptionVisible = false;
            e.Handled = true;
        }
    }

    private static async void TextBox_Drop(object sender, DragEventArgs e)
    {
        if (sender is not TextBox textBox || !e.DataView.Contains(DataFormat))
        {
            return;
        }

        try
        {
            MoveInsertionCaret(textBox, e.GetPosition(textBox));
            var value = await e.DataView.GetDataAsync(DataFormat);
            var name = value as string ?? string.Empty;
            var insertion = VariableReference.Insert(
                textBox.Text,
                textBox.SelectionStart,
                textBox.SelectionLength,
                name);
            if (!insertion.Succeeded)
            {
                return;
            }

            textBox.Text = insertion.Text;
            textBox.SelectionStart = insertion.CaretIndex;
            textBox.SelectionLength = 0;
            textBox.Focus(FocusState.Programmatic);
            e.Handled = true;
        }
        catch
        {
            // Invalid cross-application data is ignored; only ProbeLoom variable drags are accepted.
        }
    }

    private static void MoveInsertionCaret(TextBox textBox, Windows.Foundation.Point point)
    {
        var index = FindNearestCaretIndex(textBox, point);
        textBox.Focus(FocusState.Programmatic);
        textBox.SelectionStart = index;
        textBox.SelectionLength = 0;
    }

    private static int FindNearestCaretIndex(TextBox textBox, Windows.Foundation.Point point)
    {
        var length = textBox.Text?.Length ?? 0;
        if (length == 0)
        {
            return 0;
        }

        var sampleStep = Math.Max(1, length / 512);
        var bestIndex = 0;
        var bestDistance = double.MaxValue;

        for (var index = 0; index <= length; index += sampleStep)
        {
            Consider(index);
        }

        Consider(length);
        var searchStart = Math.Max(0, bestIndex - sampleStep);
        var searchEnd = Math.Min(length, bestIndex + sampleStep);
        for (var index = searchStart; index <= searchEnd; index++)
        {
            Consider(index);
        }

        return bestIndex;

        void Consider(int index)
        {
            try
            {
                var rectangle = index == length
                    ? textBox.GetRectFromCharacterIndex(length - 1, true)
                    : textBox.GetRectFromCharacterIndex(index, false);
                var verticalDistance = point.Y < rectangle.Top
                    ? rectangle.Top - point.Y
                    : point.Y > rectangle.Bottom
                        ? point.Y - rectangle.Bottom
                        : 0;
                var horizontalDistance = Math.Abs(point.X - rectangle.X);
                var distance = (verticalDistance * 10_000) + horizontalDistance;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestIndex = index;
                }
            }
            catch (ArgumentException)
            {
                // Text layout can briefly lag behind Text during rapid editing.
            }
        }
    }
}
