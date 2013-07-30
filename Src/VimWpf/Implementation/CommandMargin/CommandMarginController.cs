﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using Microsoft.FSharp.Core;
using Microsoft.VisualStudio.Text.Classification;
using Vim.Extensions;
using Vim.UI.Wpf.Properties;
using WpfKeyboard = System.Windows.Input.Keyboard;

namespace Vim.UI.Wpf.Implementation.CommandMargin
{
    /// <summary>
    /// The type of edit that we are currently performing.  None exists when no command line edit
    /// </summary>
    internal enum EditKind
    {
        None,
        Command,
        SearchForward,
        SearchBackward
    }

    internal sealed class CommandMarginController
    {
        private readonly IVimBuffer _vimBuffer;
        private readonly CommandMarginControl _margin;
        private readonly IEditorFormatMap _editorFormatMap;
        private readonly ReadOnlyCollection<Lazy<IOptionsProviderFactory>> _optionsProviderFactory;
        private readonly FrameworkElement _parentVisualElement;
        private bool _inKeyInputEvent;
        private bool _inCommandUpdate;
        private string _message;
        private SwitchModeEventArgs _modeSwitchEventArgs;
        private EditKind _editKind;

        /// <summary>
        /// We need to hold a reference to Text Editor visual element.
        /// </summary>
        internal FrameworkElement ParentVisualElement
        {
            get { return _parentVisualElement; }
        }

        internal EditKind CommandLineEditKind
        {
            get { return _editKind; }
        }

        internal CommandMarginController(IVimBuffer buffer, FrameworkElement parentVisualElement, CommandMarginControl control, IEditorFormatMap editorFormatMap, IEnumerable<Lazy<IOptionsProviderFactory>> optionsProviderFactory)
        {
            _vimBuffer = buffer;
            _margin = control;
            _parentVisualElement = parentVisualElement;
            _editorFormatMap = editorFormatMap;
            _optionsProviderFactory = optionsProviderFactory.ToList().AsReadOnly();

            _vimBuffer.SwitchedMode += OnSwitchMode;
            _vimBuffer.KeyInputStart += OnKeyInputStart;
            _vimBuffer.KeyInputEnd += OnKeyInputEnd;
            _vimBuffer.StatusMessage += OnStatusMessage;
            _vimBuffer.ErrorMessage += OnErrorMessage;
            _vimBuffer.WarningMessage += OnWarningMessage;
            _vimBuffer.CommandMode.CommandChanged += OnCommandChanged;
            _vimBuffer.Vim.MacroRecorder.RecordingStarted += OnRecordingStarted;
            _vimBuffer.Vim.MacroRecorder.RecordingStopped += OnRecordingStopped;
            _margin.OptionsButton.Click += OnOptionsClicked;
            _margin.CommandLineTextBox.PreviewKeyDown += OnCommandLineTextBoxPreviewKeyDown;
            _margin.CommandLineTextBox.TextChanged += OnCommandLineTextBoxTextChanged;
            _margin.CommandLineTextBox.SelectionChanged += OnCommandLineTextBoxSelectionChanged;
            _margin.CommandLineTextBox.LostKeyboardFocus += CommandLineTextBoxLostKeyboardFocus;
            _editorFormatMap.FormatMappingChanged += OnFormatMappingChanged;
            UpdateForRecordingChanged();
            UpdateTextColor();
        }

        private void ChangeEditKind(EditKind editKind)
        {
            if (editKind == _editKind)
            {
                return;
            }

            _editKind = editKind;
            switch (editKind)
            {
                case EditKind.None:
                    // Make sure that the editor has focus 
                    if (ParentVisualElement != null)
                    {
                        ParentVisualElement.Focus();
                    }
                    _margin.IsEditReadOnly = true;
                    break;
                case EditKind.Command:
                case EditKind.SearchForward:
                case EditKind.SearchBackward:
                    WpfKeyboard.Focus(_margin.CommandLineTextBox);
                    _margin.IsEditReadOnly = false;
                    break;
                default:
                    Contract.FailEnumValue(editKind);
                    break;
            }
        }

        internal void Disconnect()
        {
            _vimBuffer.CommandMode.CommandChanged -= OnCommandChanged;
            _vimBuffer.Vim.MacroRecorder.RecordingStarted -= OnRecordingStarted;
            _vimBuffer.Vim.MacroRecorder.RecordingStopped -= OnRecordingStopped;
        }

        private void KeyInputEventComplete()
        {
            _inKeyInputEvent = false;

            try
            {
                if (!String.IsNullOrEmpty(_message))
                {
                    _margin.StatusLine = _message;
                }
                else if (_modeSwitchEventArgs != null)
                {
                    UpdateForSwitchMode(_modeSwitchEventArgs.PreviousMode, _modeSwitchEventArgs.CurrentMode);
                }
                else
                {
                    UpdateForNoEvent();
                }
            }
            finally
            {
                _message = null;
                _modeSwitchEventArgs = null;
            }
        }

        private void MessageEvent(string message)
        {
            if (_inKeyInputEvent)
            {
                _message = message;
            }
            else
            {
                _margin.StatusLine = message;
            }
        }

        private void UpdateForSwitchMode(FSharpOption<IMode> previousMode, IMode currentMode)
        {
            // Calculate the argument string if we are in one time command mode
            string oneTimeArgument = null;
            if (_vimBuffer.InOneTimeCommand.IsSome())
            {
                if (_vimBuffer.InOneTimeCommand.Is(ModeKind.Insert))
                {
                    oneTimeArgument = "insert";
                }
                else if (_vimBuffer.InOneTimeCommand.Is(ModeKind.Replace))
                {
                    oneTimeArgument = "replace";
                }
            }

            // Check if we can enable the command line to accept user input
            var search = _vimBuffer.IncrementalSearch;

            switch (currentMode.ModeKind)
            {
                case ModeKind.Normal:
                    _margin.StatusLine = String.IsNullOrEmpty(oneTimeArgument)
                        ? String.Empty
                        : String.Format(Resources.NormalOneTimeCommandBanner, oneTimeArgument);
                    break;
                case ModeKind.Command:
                    _margin.StatusLine = ":" + _vimBuffer.CommandMode.Command;
                    break;
                case ModeKind.Insert:
                    _margin.StatusLine = Resources.InsertBanner;
                    break;
                case ModeKind.Replace:
                    _margin.StatusLine = Resources.ReplaceBanner;
                    break;
                case ModeKind.VisualBlock:
                    _margin.StatusLine = String.IsNullOrEmpty(oneTimeArgument)
                        ? Resources.VisualBlockBanner
                        : String.Format(Resources.VisualBlockOneTimeCommandBanner, oneTimeArgument);
                    break;
                case ModeKind.VisualCharacter:
                    _margin.StatusLine = String.IsNullOrEmpty(oneTimeArgument)
                        ? Resources.VisualCharacterBanner
                        : String.Format(Resources.VisualCharacterOneTimeCommandBanner, oneTimeArgument);
                    break;
                case ModeKind.VisualLine:
                    _margin.StatusLine = String.IsNullOrEmpty(oneTimeArgument)
                        ? Resources.VisualLineBanner
                        : String.Format(Resources.VisualLineOneTimeCommandBanner, oneTimeArgument);
                    break;
                case ModeKind.SelectBlock:
                    _margin.StatusLine = Resources.SelectBlockBanner;
                    break;
                case ModeKind.SelectCharacter:
                    _margin.StatusLine = Resources.SelectCharacterBanner;
                    break;
                case ModeKind.SelectLine:
                    _margin.StatusLine = Resources.SelectLineBanner;
                    break;
                case ModeKind.ExternalEdit:
                    _margin.StatusLine = Resources.ExternalEditBanner;
                    break;
                case ModeKind.Disabled:
                    _margin.StatusLine = _vimBuffer.DisabledMode.HelpMessage;
                    break;
                case ModeKind.SubstituteConfirm:
                    UpdateSubstituteConfirmMode();
                    break;
                default:
                    _margin.StatusLine = String.Empty;
                    break;
            }
        }

        /// <summary>
        /// Update the status line at the end of a key press event which didn't result in 
        /// a mode change
        /// </summary>
        private void UpdateForNoEvent()
        {
            var search = _vimBuffer.IncrementalSearch;
            if (search.InSearch && search.CurrentSearchData.IsSome())
            {
                var data = search.CurrentSearchData.Value;
                var prefix = data.Kind.IsAnyForward ? "/" : "?";

                //TODO: Workaround to fix strange character when pressing <Home>...
                _margin.StatusLine = prefix + data.Pattern.Trim('\0');
                return;
            }

            switch (_vimBuffer.ModeKind)
            {
                case ModeKind.Command:
                    //TODO: Workaround to fix strange character when pressing <Home>...
                    _margin.StatusLine = ":" + _vimBuffer.CommandMode.Command.Trim('\0'); ;
                    break;
                case ModeKind.Normal:
                    _margin.StatusLine = _vimBuffer.NormalMode.Command;
                    break;
                case ModeKind.SubstituteConfirm:
                    UpdateSubstituteConfirmMode();
                    break;
                case ModeKind.Disabled:
                    _margin.StatusLine = _vimBuffer.DisabledMode.HelpMessage;
                    break;
                case ModeKind.VisualBlock:
                    _margin.StatusLine = Resources.VisualBlockBanner;
                    break;
                case ModeKind.VisualCharacter:
                    _margin.StatusLine = Resources.VisualCharacterBanner;
                    break;
                case ModeKind.VisualLine:
                    _margin.StatusLine = Resources.VisualLineBanner;
                    break;
            }
        }

        private void UpdateForRecordingChanged()
        {
            _margin.IsRecording = _vimBuffer.Vim.MacroRecorder.IsRecording
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void UpdateSubstituteConfirmMode()
        {
            var replace = _vimBuffer.SubstituteConfirmMode.CurrentSubstitute.SomeOrDefault("");
            _margin.StatusLine = String.Format(Resources.SubstituteConfirmBannerFormat, replace);
        }

        /// <summary>
        /// Update the color of the editor portion of the command window to be the user
        /// defined values
        /// </summary>
        private void UpdateTextColor()
        {
            var propertyMap = _editorFormatMap.GetProperties(CommandMarginFormatDefinition.Name);
            _margin.TextForeground = propertyMap.GetForegroundBrush(SystemColors.WindowTextBrush);
            _margin.TextBackground = propertyMap.GetBackgroundBrush(SystemColors.WindowBrush);
        }

        /// <summary>
        /// This method handles the KeyInput as it applies to command line editor
        /// </summary>
        internal void HandleKeyEvent(KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Escape:
                    _vimBuffer.Process(KeyInputUtil.EscapeKey);
                    ChangeEditKind(EditKind.None);
                    break;
                case Key.Return:
                    ExecuteCommand(_margin.CommandLineTextBox.Text);
                    break;
                case Key.Up:
                    _vimBuffer.Process(KeyInputUtil.VimKeyToKeyInput(VimKey.Up));
                    break;
                case Key.Down:
                    _vimBuffer.Process(KeyInputUtil.VimKeyToKeyInput(VimKey.Down));
                    break;
            }
        }

        /// <summary>
        /// If we're in command mode and a key is processed which effects the edit we should handle
        /// it here
        /// </summary>
        private void CheckEnableCommandLineEdit(KeyInputStartEventArgs args)
        {
            if (_editKind != EditKind.None)
            {
                return;
            }

            var commandLineEditKind = CalculateCommandLineEditKind();
            if (commandLineEditKind == EditKind.None)
            {
                return;
            }

            switch (args.KeyInput.Key)
            {
                case VimKey.Home:
                    // Enable command line edition
                    ChangeEditKind(commandLineEditKind);
                    _margin.UpdateCaretPosition(moveCaretToEnd: false);
                    args.Handled = true;
                    break;
                case VimKey.Left:
                    ChangeEditKind(commandLineEditKind);
                    _margin.UpdateCaretPosition(moveCaretToEnd: true);
                    args.Handled = true;
                    break;
                case VimKey.Up:
                case VimKey.Down:
                    // User is navigation through history, move caret to the end of the entry
                    _margin.UpdateCaretPosition(moveCaretToEnd: true);
                    break;
            }
        }

        /// <summary>
        /// Update the current command to the given value
        /// </summary>
        private void UpdateCommand(string command)
        {
            _inCommandUpdate = true;
            try
            {
                command = command ?? "";
                switch (_editKind)
                {
                    case EditKind.Command:

                        if (_vimBuffer.ModeKind == ModeKind.Command)
                        {
                            _vimBuffer.CommandMode.Command = command;
                        }
                        break;
                    case EditKind.SearchBackward:
                        if (_vimBuffer.IncrementalSearch.InSearch)
                        {
                            var pattern = command.Length > 0 && command[0] == '?'
                                ? command.Substring(1)
                                : command;
                            _vimBuffer.IncrementalSearch.ResetSearch(pattern);
                        }
                        break;
                    case EditKind.SearchForward:
                        if (_vimBuffer.IncrementalSearch.InSearch)
                        {
                            var pattern = command.Length > 0 && command[0] == '/'
                                ? command.Substring(1)
                                : command;
                            _vimBuffer.IncrementalSearch.ResetSearch(pattern);
                        }
                        break;
                    case EditKind.None:
                        break;
                    default:
                        Contract.FailEnumValue(_editKind);
                        break;
                }
            }
            finally
            {
                _inCommandUpdate = false;
            }
        }

        /// <summary>
        /// Execute the command and switch focus back to the editor
        /// </summary>
        private void ExecuteCommand(string command)
        {
            if (_editKind == EditKind.None)
            {
                return;
            }

            UpdateCommand(command);
            _vimBuffer.Process(KeyInputUtil.EnterKey);
            ChangeEditKind(EditKind.None);
        }

        #region Event Handlers

        private void OnSwitchMode(object sender, SwitchModeEventArgs args)
        {
            if (_inKeyInputEvent)
            {
                _modeSwitchEventArgs = args;
            }
            else
            {
                UpdateForSwitchMode(args.PreviousMode, args.CurrentMode);
            }
        }

        private void OnKeyInputStart(object sender, KeyInputStartEventArgs args)
        {
            _inKeyInputEvent = true;
            CheckEnableCommandLineEdit(args);
        }

        private void OnKeyInputEnd(object sender, KeyInputEventArgs args)
        {
            KeyInputEventComplete();
        }

        private void OnStatusMessage(object sender, StringEventArgs args)
        {
            MessageEvent(args.Message);
        }

        private void OnErrorMessage(object sender, StringEventArgs args)
        {
            MessageEvent(args.Message);
        }

        private void OnWarningMessage(object sender, StringEventArgs args)
        {
            MessageEvent(args.Message);
        }

        private void OnOptionsClicked(object sender, EventArgs e)
        {
            var provider = _optionsProviderFactory.Select(x => x.Value.CreateOptionsProvider()).Where(x => x != null).FirstOrDefault();
            if (provider != null)
            {
                provider.ShowDialog(_vimBuffer);
            }
            else
            {
                MessageBox.Show("No options provider available");
            }
        }

        private void OnFormatMappingChanged(object sender, FormatItemsEventArgs e)
        {
            UpdateTextColor();
        }

        private void OnRecordingStarted(object sender, RecordRegisterEventArgs args)
        {
            UpdateForRecordingChanged();
        }

        private void OnRecordingStopped(object sender, EventArgs e)
        {
            UpdateForRecordingChanged();
        }

        private void OnCommandChanged(object sender, EventArgs e)
        {
            if (_inCommandUpdate)
            {
                return;
            }

            UpdateForNoEvent();
        }

        private void OnCommandLineTextBoxPreviewKeyDown(object sender, KeyEventArgs e)
        {
            HandleKeyEvent(e);
        }

        private void OnCommandLineTextBoxTextChanged(object sender, RoutedEventArgs e)
        {
            UpdateCommand(_margin.CommandLineTextBox.Text);
        }

        /// <summary>
        /// If the user selects the text with the mouse then we need to initiate an edit 
        /// in the case the vim buffer is capable of one.  If not then we need to cancel
        /// the selection.  Anything else will give the user the appearance that they can
        /// edit the text when in fact they cannot
        /// </summary>
        private void OnCommandLineTextBoxSelectionChanged(object sender, RoutedEventArgs e)
        {
            var textBox = _margin.CommandLineTextBox;
            if (string.IsNullOrEmpty(textBox.SelectedText))
            {
                return;
            }

            var kind = CalculateCommandLineEditKind();
            if (kind != EditKind.None)
            {
                ChangeEditKind(kind);
            }
        }

        private void CommandLineTextBoxLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            ChangeEditKind(EditKind.None);
        }

        /// <summary>
        /// Calculate the type of edit that should be performed based on the current state of the
        /// IVimBuffer
        /// </summary>
        private EditKind CalculateCommandLineEditKind()
        {
            if (_vimBuffer.ModeKind == ModeKind.Command)
            {
                return EditKind.Command;
            }

            if (_vimBuffer.IncrementalSearch.InSearch &&
                _vimBuffer.IncrementalSearch.CurrentSearchData.IsSome())
            {
                return _vimBuffer.IncrementalSearch.CurrentSearchData.Value.Kind.IsAnyForward
                    ? EditKind.SearchForward
                    : EditKind.SearchBackward;
            }

            return EditKind.None;
        }

        #endregion
    }
}
