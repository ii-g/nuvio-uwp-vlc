using System;
using Nuvio.Services;
using Windows.ApplicationModel.Resources;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;

namespace Nuvio.Input
{
    /// <summary>Implemented by pages that handle controller actions before the router's global handling.</summary>
    internal interface IPageInputHandler
    {
        /// <summary>Returns true if the action was handled and must not fall through.</summary>
        bool HandleAction(string action, bool repeat);
    }

    /// <summary>
    /// Named controller actions → focus moves / page back / global handling (architecture §4).
    /// ControllerBridge polling code is unchanged; its callback becomes this router instead of
    /// AppBridge.SendInput. Gamepad A ("select") is intentionally NOT handled here: XAML
    /// activates the focused control natively (MainPage leaves GamepadA unswallowed).
    /// </summary>
    internal sealed class NativeInputRouter
    {
        private readonly MainPage _shell;
        private readonly Frame _frame;
        private readonly NavigationService _navigation;
        private readonly ResourceLoader _strings = ResourceLoader.GetForViewIndependentUse("Resources");
        private DateTime _lastBackPress = DateTime.MinValue;
        // Web focus.js dedupes the same action arriving twice within 150 ms (one physical
        // press can reach the app through the poll and through native key handling); mirror
        // it here so a single tap can never move focus twice. Held repeats arrive every
        // 300 ms (ControllerBridge), so they always pass this window.
        private const double DirectionDedupeMs = 150;
        private string _lastDirection;
        private DateTime _lastDirectionTime = DateTime.MinValue;

        public NativeInputRouter(MainPage shell, Frame frame, NavigationService navigation)
        {
            _shell = shell;
            _frame = frame;
            _navigation = navigation;
        }

        public void Handle(string action, bool repeat)
        {
            if (_shell.DialogOpen) return;
            if (action == "up" || action == "down" || action == "left" || action == "right")
            {
                var now = DateTime.UtcNow;
                if (action == _lastDirection && (now - _lastDirectionTime).TotalMilliseconds < DirectionDedupeMs)
                {
                    return;
                }
                _lastDirection = action;
                _lastDirectionTime = now;
            }
            var page = _frame.Content as Page;
            var handler = page as IPageInputHandler;
            if (handler != null && handler.HandleAction(action, repeat)) return;
            switch (action)
            {
                case "up":
                    MoveFocus(FocusNavigationDirection.Up);
                    break;
                case "down":
                    MoveFocus(FocusNavigationDirection.Down);
                    break;
                case "left":
                    MoveFocus(FocusNavigationDirection.Left);
                    break;
                case "right":
                    MoveFocus(FocusNavigationDirection.Right);
                    break;
                case "select":
                    // XAML activates the focused element natively; nothing to do here.
                    break;
                case "back":
                    HandleBack();
                    break;
                case "x":
                case "y":
                case "menu":
                case "view":
                case "lb":
                case "rb":
                case "lt":
                case "rt":
                    // Ignored outside the player (player is M4), except y/menu on a focused
                    // Continue Watching card, which the Home page handles above.
                    break;
            }
        }

        private void MoveFocus(FocusNavigationDirection direction)
        {
            if (FocusManager.GetFocusedElement() == null)
            {
                var first = FocusManager.FindFirstFocusableElement(_frame) as Control;
                if (first != null)
                {
                    first.Focus(FocusState.Programmatic);
                    return;
                }
            }
            FocusManager.TryMoveFocus(direction);
        }

        private void HandleBack()
        {
            // A dialog open means the controller is paused and XAML handles back; this never runs then.
            // The current page already had its say via IPageInputHandler above.
            if (_frame.CanGoBack)
            {
                _frame.GoBack();
                return;
            }
            if (!(_frame.Content is Views.HomePage))
            {
                _navigation.NavigateHome();
                return;
            }
            var now = DateTime.UtcNow;
            if ((now - _lastBackPress).TotalMilliseconds <= 2500)
            {
                Application.Current.Exit();
                return;
            }
            _lastBackPress = now;
            var message = _strings.GetString("PressBackAgainToExit");
            _shell.ShowToast(string.IsNullOrEmpty(message) ? "Press back again to exit" : message, "info", 2500);
        }
    }
}
