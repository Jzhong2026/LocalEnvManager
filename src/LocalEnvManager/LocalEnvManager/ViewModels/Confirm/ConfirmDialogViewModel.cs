using Caliburn.Micro;

namespace LocalEnvManager.ViewModels
{
    /// <summary>
    /// View-model for the reusable confirmation dialog. Returns <c>true</c>
    /// when the user clicks "确认", <c>false</c> when they click "取消".
    ///
    /// Launched via <c>IWindowManager.ShowDialogAsync(vm)</c>; the bool result
    /// is whatever was passed to <see cref="TryCloseAsync(bool?)"/>.
    /// </summary>
    public sealed class ConfirmDialogViewModel : Screen
    {
        public ConfirmDialogViewModel(string message, string title = "请确认")
        {
            Message = message;
            DisplayName = title;
        }

        public string Message { get; }

        public void Confirm() => TryCloseAsync(true);

        public void Cancel() => TryCloseAsync(false);
    }
}