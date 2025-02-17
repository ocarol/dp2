﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace dp2SSL
{
    /// <summary>
    /// SubmitWindow.xaml 的交互逻辑
    /// </summary>
    public partial class SubmitWindow : Window
    {
        // public event EventHandler Next;

        List<DisplayContent> _contents = new List<DisplayContent>();

        int _showCount = 0;

        public SubmitWindow()
        {
            InitializeComponent();

            Loaded += SubmitWindow_Loaded;
            Unloaded += SubmitWindow_Unloaded;
        }

        private void SubmitWindow_Unloaded(object sender, RoutedEventArgs e)
        {
            InputManager.Current.PreProcessInput -= OnActivity;
            if (_activityTimer != null)
                _activityTimer.Tick -= OnInactivity;
        }

        private void SubmitWindow_Loaded(object sender, RoutedEventArgs e)
        {
            SetIdleEvents();
        }

        class DisplayContent
        {
            public string Color { get; set; }
            public string Text { get; set; }
            // public MessageDocument Document { get; set; }
            public SubmitDocument Document { get; set; }
        }

        public void PushContent(string text, string color)
        {
            _contents.Add(new DisplayContent
            {
                Text = text,
                Color = color
            });
        }

#if NO
        public void PushContent(MessageDocument doc)
        {
            _contents.Add(new DisplayContent
            {
                Document = doc
            });
            // 变化按钮文字
            RefreshButtonText();
        }
#endif

        public void PushContent(SubmitDocument doc)
        {
            _contents.Add(new DisplayContent
            {
                Document = doc
            });
            // 变化按钮文字
            RefreshButtonText();
        }

        DisplayContent PullContent()
        {
            if (_contents.Count > 0)
            {
                var content = _contents[0];
                _contents.RemoveAt(0);
                // 变化按钮文字
                RefreshButtonText();
                return content;
            }

            return null;
        }

        void RefreshButtonText()
        {
            if (_contents.Count > 0)
            {
                this.okButton.Content = $"继续 ({_contents.Count})";
            }
            else
            {
                this.okButton.Content = $"关闭";
            }
        }

        public void ShowContent()
        {
            //if (_contents.Count == 0)
            //    return;
            if (_showCount > 0)
                return;
            var first = PullContent();
            if (first == null)
            {
                this.MessageText = "(blank)";
                this.BackColor = "yellow";
                return;
            }

            if (first.Document != null)
            {
#if NO
                string speak = "";
                Application.Current.Dispatcher.Invoke(new Action(() =>
                {
                    this.MessageDocument = first.Document.BuildDocument(
                        dp2SSL.MessageDocument.BaseFontSize/*18*/,
                        "",
                        out speak);
                }));
                if (string.IsNullOrEmpty(speak) == false)
                    App.CurrentApp.Speak(speak);
#endif
                App.Invoke(new Action(() =>
                {
                    this.MessageDocument = first.Document;
                }));
            }
            else
            {
                this.MessageText = first.Text;
                this.BackColor = first.Color;
            }

            _showCount++;
        }

        public void Refresh(List<ActionInfo> actions)
        {
            // 先刷新当前文档
            SubmitDocument current = this.MessageDocument as SubmitDocument;
            if (current != null)
                current.Refresh(actions);

            // 然后刷新正在排队的文档
            foreach (var content in _contents)
            {
                content.Document?.Refresh(actions);
            }
        }

        public string MessageText
        {
            get
            {
                return text.Text;
            }
            set
            {
                text.Text = value;
                if (value != null)
                {
                    text.Visibility = Visibility.Visible;
                    richText.Visibility = Visibility.Collapsed;
                }
            }
        }

        public FlowDocument MessageDocument
        {
            get
            {
                return richText.Document;
            }
            set
            {
                /*
                var old = richText.Document;
                richText.Document = null;
                */
                richText.Document = value;
                if (value != null)
                {
                    if (text.Visibility != Visibility.Collapsed)
                        text.Visibility = Visibility.Collapsed;
                    if (richText.Visibility != Visibility.Visible)
                        richText.Visibility = Visibility.Visible;
                }
            }
        }

        public ProgressBar ProgressBar
        {
            get
            {
                return progressBar;
            }
        }

        string _backColor = "black";
        public string BackColor
        {
            get
            {
                return _backColor;
            }
            set
            {
                _backColor = value;
                if (_backColor == "black")
                {
                    this.Background = Brushes.Black;
                    this.Foreground = Brushes.White;
                }
                if (_backColor == "red")
                {
                    this.Background = Brushes.DarkRed;
                    this.Foreground = Brushes.White;
                }
                if (_backColor == "yellow")
                {
                    this.Background = Brushes.DarkOrange;
                    this.Foreground = Brushes.White;
                }
                if (_backColor == "green")
                {
                    this.Background = Brushes.DarkGreen;
                    this.Foreground = Brushes.White;
                }
                if (_backColor == "gray")
                {
                    this.Background = Brushes.DarkGray;
                    this.Foreground = Brushes.White;
                }
            }
        }

        public string TitleText
        {
            get
            {
                return title.Text;
            }
            set
            {
                title.Text = value;
            }
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            // Next?.Invoke(sender, new EventArgs());

            if (_contents.Count == 0)
            {
                // TODO: 如果窗口正在处理中，要避免被关闭
                // this.Close();
                this.MessageDocument = null;
                this.MessageText = "";
                this.Hide();
                _showCount = 0;
                return;
            }

            _showCount = 0;
            ShowContent();
        }

        private void _progressWindow_Next(object sender, EventArgs e)
        {

        }

        private DispatcherTimer _activityTimer;
        private Point _inactiveMousePosition = new Point(0, 0);

        public void SetIdleEvents()
        {
            InputManager.Current.PreProcessInput -= OnActivity;
            if (_activityTimer != null)
                _activityTimer.Tick -= OnInactivity;

            var seconds = ShelfData.GetIdleCloseSubmitDialog();
            if (seconds > 0)
            {
                InputManager.Current.PreProcessInput += OnActivity;
                _activityTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds), IsEnabled = true };
                _activityTimer.Tick += OnInactivity;
            }
        }

        void OnActivity(object sender, PreProcessInputEventArgs e)
        {
            InputEventArgs inputEventArgs = e.StagingItem.Input;

            if (inputEventArgs is MouseEventArgs || inputEventArgs is KeyboardEventArgs)
            {
                if (e.StagingItem.Input is MouseEventArgs)
                {
                    MouseEventArgs mouseEventArgs = (MouseEventArgs)e.StagingItem.Input;

                    // no button is pressed and the position is still the same as the application became inactive
                    if (mouseEventArgs.LeftButton == MouseButtonState.Released &&
                        mouseEventArgs.RightButton == MouseButtonState.Released &&
                        mouseEventArgs.MiddleButton == MouseButtonState.Released &&
                        mouseEventArgs.XButton1 == MouseButtonState.Released &&
                        mouseEventArgs.XButton2 == MouseButtonState.Released &&
                        _inactiveMousePosition == mouseEventArgs.GetPosition(this))
                        return;
                }

                // Debug.WriteLine(inputEventArgs.ToString());

                /*
                // set UI on activity
                rectangle.Visibility = Visibility.Visible;
                */

                ResetActivityTimer();
            }
        }

        void OnInactivity(object sender, EventArgs e)
        {
            // remember mouse position
            _inactiveMousePosition = Mouse.GetPosition(this);

            if (PageMenu.PageShelf.IsPatronEmpty() == true
                && ShelfData.OpeningDoorCount == 0
                && DoorStateTask.CopyList().Count == 0)
            {
                // 关闭窗口
                this.Close();
            }
        }

        public void ResetActivityTimer()
        {
            _activityTimer?.Stop();
            _activityTimer?.Start();
        }
    }
}
