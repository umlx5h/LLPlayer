using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace LLPlayer.Extensions;


// ref: https://stackoverflow.com/a/50905766/9070784
public static class TextBoxHelper
{
    #region Enum Declarations
    public enum NumericFormat
    {
        Double,
        Int,
        Uint,
        Natural
    }

    public enum EvenOddConstraint
    {
        All,
        OnlyEven,
        OnlyOdd
    }

    #endregion

    #region Dependency Properties & CLR Wrappers

    public static readonly DependencyProperty OnlyNumericProperty =
        DependencyProperty.RegisterAttached("OnlyNumeric", typeof(NumericFormat?), typeof(TextBoxHelper),
            new PropertyMetadata(null, DependencyPropertiesChanged));
    public static void SetOnlyNumeric(TextBox element, NumericFormat value) =>
        element.SetValue(OnlyNumericProperty, value);
    public static NumericFormat GetOnlyNumeric(TextBox element) =>
        (NumericFormat)element.GetValue(OnlyNumericProperty);


    public static readonly DependencyProperty DefaultValueProperty =
        DependencyProperty.RegisterAttached("DefaultValue", typeof(string), typeof(TextBoxHelper),
            new PropertyMetadata(null, DependencyPropertiesChanged));
    public static void SetDefaultValue(TextBox element, string value) =>
        element.SetValue(DefaultValueProperty, value);
    public static string GetDefaultValue(TextBox element) => (string)element.GetValue(DefaultValueProperty);


    public static readonly DependencyProperty EvenOddConstraintProperty =
        DependencyProperty.RegisterAttached("EvenOddConstraint", typeof(EvenOddConstraint), typeof(TextBoxHelper),
            new PropertyMetadata(EvenOddConstraint.All, DependencyPropertiesChanged));
    public static void SetEvenOddConstraint(TextBox element, EvenOddConstraint value) =>
        element.SetValue(EvenOddConstraintProperty, value);
    public static EvenOddConstraint GetEvenOddConstraint(TextBox element) =>
        (EvenOddConstraint)element.GetValue(EvenOddConstraintProperty);

    #endregion

    #region Dependency Properties Methods

    private static void DependencyPropertiesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (!(d is TextBox textBox))
            throw new Exception("Attached property must be used with TextBox.");

        switch (e.Property.Name)
        {
            case "OnlyNumeric":
                {
                    var castedValue = (NumericFormat?)e.NewValue;

                    if (castedValue.HasValue)
                    {
                        textBox.PreviewTextInput += TextBox_PreviewTextInput;
                        DataObject.AddPastingHandler(textBox, TextBox_PasteEventHandler);
                    }
                    else
                    {
                        textBox.PreviewTextInput -= TextBox_PreviewTextInput;
                        DataObject.RemovePastingHandler(textBox, TextBox_PasteEventHandler);
                    }

                    break;
                }

            case "DefaultValue":
                {
                    var castedValue = (string)e.NewValue;

                    if (castedValue != null)
                    {
                        textBox.TextChanged += TextBox_TextChanged;
                    }
                    else
                    {
                        textBox.TextChanged -= TextBox_TextChanged;
                    }

                    break;
                }
        }
    }

    #endregion

    private static void TextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        var textBox = (TextBox)sender;

        string newText;

        if (textBox.SelectionLength == 0)
        {
            newText = textBox.Text.Insert(textBox.SelectionStart, e.Text);
        }
        else
        {
            var textAfterDelete = textBox.Text.Remove(textBox.SelectionStart, textBox.SelectionLength);

            newText = textAfterDelete.Insert(textBox.SelectionStart, e.Text);
        }

        var evenOddConstraint = GetEvenOddConstraint(textBox);

        switch (GetOnlyNumeric(textBox))
        {
            case NumericFormat.Double:
                {
                    if (double.TryParse(newText, out double number))
                    {
                        switch (evenOddConstraint)
                        {
                            case EvenOddConstraint.OnlyEven:

                                if (number % 2 != 0)
                                    e.Handled = true;
                                else
                                    e.Handled = false;

                                break;

                            case EvenOddConstraint.OnlyOdd:

                                if (number % 2 == 0)
                                    e.Handled = true;
                                else
                                    e.Handled = false;

                                break;
                        }
                    }
                    else
                        e.Handled = true;

                    break;
                }

            case NumericFormat.Int:
                {
                    if (int.TryParse(newText, out int number))
                    {
                        switch (evenOddConstraint)
                        {
                            case EvenOddConstraint.OnlyEven:

                                if (number % 2 != 0)
                                    e.Handled = true;
                                else
                                    e.Handled = false;

                                break;

                            case EvenOddConstraint.OnlyOdd:

                                if (number % 2 == 0)
                                    e.Handled = true;
                                else
                                    e.Handled = false;

                                break;
                        }
                    }
                    else
                        e.Handled = true;

                    break;
                }

            case NumericFormat.Uint:
                {
                    if (uint.TryParse(newText, out uint number))
                    {
                        switch (evenOddConstraint)
                        {
                            case EvenOddConstraint.OnlyEven:

                                if (number % 2 != 0)
                                    e.Handled = true;
                                else
                                    e.Handled = false;

                                break;

                            case EvenOddConstraint.OnlyOdd:

                                if (number % 2 == 0)
                                    e.Handled = true;
                                else
                                    e.Handled = false;

                                break;
                        }
                    }
                    else
                        e.Handled = true;

                    break;
                }

            case NumericFormat.Natural:
                {
                    if (uint.TryParse(newText, out uint number))
                    {
                        if (number == 0)
                            e.Handled = true;
                        else
                        {
                            switch (evenOddConstraint)
                            {
                                case EvenOddConstraint.OnlyEven:

                                    if (number % 2 != 0)
                                        e.Handled = true;
                                    else
                                        e.Handled = false;

                                    break;

                                case EvenOddConstraint.OnlyOdd:

                                    if (number % 2 == 0)
                                        e.Handled = true;
                                    else
                                        e.Handled = false;

                                    break;
                            }
                        }
                    }
                    else
                        e.Handled = true;

                    break;
                }
        }
    }

    private static void TextBox_PasteEventHandler(object sender, DataObjectPastingEventArgs e)
    {
        var textBox = (TextBox)sender;

        if (e.DataObject.GetDataPresent(typeof(string)))
        {
            var clipboardText = (string)e.DataObject.GetData(typeof(string));

            var newText = textBox.Text.Insert(textBox.SelectionStart, clipboardText);

            var evenOddConstraint = GetEvenOddConstraint(textBox);

            switch (GetOnlyNumeric(textBox))
            {
                case NumericFormat.Double:
                    {
                        if (double.TryParse(newText, out double number))
                        {
                            switch (evenOddConstraint)
                            {
                                case EvenOddConstraint.OnlyEven:

                                    if (number % 2 != 0)
                                        e.CancelCommand();

                                    break;

                                case EvenOddConstraint.OnlyOdd:

                                    if (number % 2 == 0)
                                        e.CancelCommand();

                                    break;
                            }
                        }
                        else
                            e.CancelCommand();

                        break;
                    }

                case NumericFormat.Int:
                    {
                        if (int.TryParse(newText, out int number))
                        {
                            switch (evenOddConstraint)
                            {
                                case EvenOddConstraint.OnlyEven:

                                    if (number % 2 != 0)
                                        e.CancelCommand();

                                    break;

                                case EvenOddConstraint.OnlyOdd:

                                    if (number % 2 == 0)
                                        e.CancelCommand();


                                    break;
                            }
                        }
                        else
                            e.CancelCommand();

                        break;
                    }

                case NumericFormat.Uint:
                    {
                        if (uint.TryParse(newText, out uint number))
                        {
                            switch (evenOddConstraint)
                            {
                                case EvenOddConstraint.OnlyEven:

                                    if (number % 2 != 0)
                                        e.CancelCommand();

                                    break;

                                case EvenOddConstraint.OnlyOdd:

                                    if (number % 2 == 0)
                                        e.CancelCommand();


                                    break;
                            }
                        }
                        else
                            e.CancelCommand();

                        break;
                    }

                case NumericFormat.Natural:
                    {
                        if (uint.TryParse(newText, out uint number))
                        {
                            if (number == 0)
                                e.CancelCommand();
                            else
                            {
                                switch (evenOddConstraint)
                                {
                                    case EvenOddConstraint.OnlyEven:

                                        if (number % 2 != 0)
                                            e.CancelCommand();

                                        break;

                                    case EvenOddConstraint.OnlyOdd:

                                        if (number % 2 == 0)
                                            e.CancelCommand();

                                        break;
                                }
                            }
                        }
                        else
                        {
                            e.CancelCommand();
                        }

                        break;
                    }
            }
        }
        else
        {
            e.CancelCommand();
        }
    }

    private static void TextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var textBox = (TextBox)sender;

        var defaultValue = GetDefaultValue(textBox);

        var evenOddConstraint = GetEvenOddConstraint(textBox);

        switch (GetOnlyNumeric(textBox))
        {
            case NumericFormat.Double:
                {
                    if (double.TryParse(textBox.Text, out double number))
                    {
                        switch (evenOddConstraint)
                        {
                            case EvenOddConstraint.OnlyEven:

                                if (number % 2 != 0)
                                    textBox.Text = defaultValue;

                                break;

                            case EvenOddConstraint.OnlyOdd:

                                if (number % 2 == 0)
                                    textBox.Text = defaultValue;

                                break;
                        }
                    }
                    else
                        textBox.Text = defaultValue;

                    break;
                }

            case NumericFormat.Int:
                {
                    if (int.TryParse(textBox.Text, out int number))
                    {
                        switch (evenOddConstraint)
                        {
                            case EvenOddConstraint.OnlyEven:

                                if (number % 2 != 0)
                                    textBox.Text = defaultValue;

                                break;

                            case EvenOddConstraint.OnlyOdd:

                                if (number % 2 == 0)
                                    textBox.Text = defaultValue;

                                break;
                        }
                    }
                    else
                        textBox.Text = defaultValue;

                    break;
                }

            case NumericFormat.Uint:
                {
                    if (uint.TryParse(textBox.Text, out uint number))
                    {
                        switch (evenOddConstraint)
                        {
                            case EvenOddConstraint.OnlyEven:

                                if (number % 2 != 0)
                                    textBox.Text = defaultValue;

                                break;

                            case EvenOddConstraint.OnlyOdd:

                                if (number % 2 == 0)
                                    textBox.Text = defaultValue;

                                break;
                        }
                    }
                    else
                        textBox.Text = defaultValue;

                    break;
                }

            case NumericFormat.Natural:
                {
                    if (uint.TryParse(textBox.Text, out uint number))
                    {
                        if (number == 0)
                            textBox.Text = defaultValue;
                        else
                        {
                            switch (evenOddConstraint)
                            {
                                case EvenOddConstraint.OnlyEven:

                                    if (number % 2 != 0)
                                        textBox.Text = defaultValue;

                                    break;

                                case EvenOddConstraint.OnlyOdd:

                                    if (number % 2 == 0)
                                        textBox.Text = defaultValue;

                                    break;
                            }
                        }
                    }
                    else
                    {
                        textBox.Text = defaultValue;
                    }

                    break;
                }
        }
    }
}
