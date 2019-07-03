﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

using DigitalPlatform;
using DigitalPlatform.Text;
using Newtonsoft.Json;

namespace dp2SSL
{
    /// <summary>
    /// PageSetting.xaml 的交互逻辑
    /// </summary>
    public partial class PageSetting : Page, INotifyPropertyChanged
    {
        public PageSetting()
        {
            InitializeComponent();

            this.DataContext = this;

            this.Loaded += PageSetting_Loaded;
            this.Unloaded += PageSetting_Unloaded;
        }

        private void PageSetting_Unloaded(object sender, RoutedEventArgs e)
        {
            // 确保 page 关闭时对话框能自动关闭
            Application.Current.Dispatcher.Invoke(new Action(() =>
            {
                foreach (var window in _dialogs)
                {
                    window.Close();
                }
            }));
        }

        private void PageSetting_Loaded(object sender, RoutedEventArgs e)
        {
            // 首次设置密码，或者登录
            InitialPage();
            if (this.password.Visibility == Visibility.Visible)
                this.password.Focus();
        }

        void InitialPage()
        {
            if (App.IsLockingPasswordEmpty())
            {
                this.passwordArea.Visibility = Visibility.Visible;
                this.buttonArea.Visibility = Visibility.Collapsed;

                this.setPassword.Visibility = Visibility.Visible;
                this.login.Visibility = Visibility.Collapsed;
            }
            else
            {
                if (HasLoggedin == false)
                {
                    this.passwordArea.Visibility = Visibility.Visible;
                    this.buttonArea.Visibility = Visibility.Collapsed;

                    this.setPassword.Visibility = Visibility.Collapsed;
                    this.login.Visibility = Visibility.Visible;
                }
                else
                {
                    this.passwordArea.Visibility = Visibility.Collapsed;
                    this.buttonArea.Visibility = Visibility.Visible;
                }
            }
        }

#if NO
        static App App
        {
            get
            {
                return ((App)Application.Current);
            }
        }
#endif

        #region 属性

#if NO
        private void Entities_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == "Borrowable")
                OnPropertyChanged(e.PropertyName);
        }
#endif

        public event PropertyChangedEventHandler PropertyChanged;

        void OnPropertyChanged(string name)
        {
            if (this.PropertyChanged != null)
                PropertyChanged(this, new PropertyChangedEventArgs(name));
        }

#if NO
        public string Borrowable
        {
            get
            {
                return booksControl.Borrowable;
            }
            set
            {
                booksControl.Borrowable = value;
            }
        }
#endif
        private bool _hasLoggedin = false;

        public bool HasLoggedin
        {
            get
            {
                return _hasLoggedin;
            }
            set
            {
                if (_hasLoggedin != value)
                {
                    _hasLoggedin = value;
                    OnPropertyChanged("HasLoggedin");
                }
            }
        }

        private string _error = null;   // "test error line asdljasdkf; ;jasldfjasdjkf aasdfasdf";

        public string Error
        {
            get => _error;
            set
            {
                if (_error != value)
                {
                    _error = value;
                    OnPropertyChanged("Error");
                }
            }
        }

        #endregion


        private void Config_Click(object sender, RoutedEventArgs e)
        {
            //FingerprintManager.Base.State = "pause";
            try
            {
                Window cfg_window = new ConfigWindow();
                cfg_window.ShowDialog();
            }
            finally
            {
                //FingerprintManager.Base.State = "";
            }

            // 迫使 URL 生效
            RfidManager.Url = App.RfidUrl;
            RfidManager.Clear();
            FingerprintManager.Url = App.FingerprintUrl;
            FingerprintManager.Clear();
            FaceManager.Url = App.FaceUrl;
            FaceManager.Clear();

            // 2019/6/19
            // 主动保存一次参数配置
            WpfClientInfo.SaveConfig();

            // 检查状态，及时报错
            {
                List<string> errors = new List<string>();
                {
                    var result = RfidManager.GetState("");
                    if (result.Value == -1)
                        errors.Add(result.ErrorInfo);
                }

                {
                    var result = FingerprintManager.GetState("");
                    if (result.Value == -1)
                        errors.Add(result.ErrorInfo);
                }

                {
                    var result = FaceManager.GetState("");
                    if (result.Value == -1)
                        errors.Add(result.ErrorInfo);

                    // 检查 Server UID

                }

                if (errors.Count > 0)
                    MessageBox.Show(StringUtil.MakePathList(errors, "\r\n"));
            }
        }

        private void ExitButton_Click(object sender, RoutedEventArgs e)
        {
            Application.Current.MainWindow.Close();
        }

        private void GoHome_Click(object sender, RoutedEventArgs e)
        {
            this.NavigationService.Navigate(new PageMenu());
        }

        // 首次设置密码
        private void SetPassword_Click(object sender, RoutedEventArgs e)
        {
            this.Error = null;

            if (string.IsNullOrEmpty(this.password.Password))
            {
                this.Error = "密码不允许为空";
                return;
            }

            App.SetLockingPassword(this.password.Password);
            InitialPage();
        }

        private void Login_Click(object sender, RoutedEventArgs e)
        {
            this.Error = null;

            if (App.MatchLockingPassword(this.password.Password) == false)
            {
                this.Error = "密码不正确，登录失败";
                return;
            }

            this.HasLoggedin = true;
            InitialPage();
        }

        private void OpenProgramFolderButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start(Environment.CurrentDirectory);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ExceptionUtil.GetAutoText(ex));
            }
        }

        private void OpenUserFolderButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start(WpfClientInfo.UserDir);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ExceptionUtil.GetAutoText(ex));
            }
        }

        private void OpenDataFolderButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start(WpfClientInfo.DataDir);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ExceptionUtil.GetAutoText(ex));
            }
        }

        private async void DownloadDailyWallpaper_Click(object sender, RoutedEventArgs e)
        {
            this.downloadDailyWallpaper.IsEnabled = false;
            try
            {
                string filename = Path.Combine(WpfClientInfo.UserDir, "daily_wallpaper");
                await DownloadBingWallPaper(filename);
            }
            finally
            {
                this.downloadDailyWallpaper.IsEnabled = true;
            }
        }

        List<Window> _dialogs = new List<Window>();

        // https://blog.csdn.net/m0_37682004/article/details/82314055
        Task DownloadBingWallPaper(string filename)
        {
            return Task.Run(() =>
            {
                using (WebClient client = new WebClient())
                {

                    ProgressWindow progress = null;
                    Application.Current.Dispatcher.Invoke(new Action(() =>
                    {
                        progress = new ProgressWindow();
                        progress.MessageText = "正在下载 bing 壁纸，请稍候 ...";
                        progress.Owner = Application.Current.MainWindow;
                        progress.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                        progress.Closed += (sender, e) =>
                        {
                            client.CancelAsync();
                        };
                        progress.Show();
                    }));

                    try
                    {
                        byte[] bytes = client.DownloadData("https://cn.bing.com/HPImageArchive.aspx?format=js&idx=0&n=1&mkt=zh-CN");
                        dynamic obj = JsonConvert.DeserializeObject(Encoding.UTF8.GetString(bytes));
                        string url = obj.images[0].url;
                        url = $"https://cn.bing.com{url}";
                        client.DownloadFile(url, filename);
                        _dialogs.Add(progress);
                        Application.Current.Dispatcher.Invoke(new Action(() =>
                        {
                            progress.MessageText = "下载完成";
                            progress.BackColor = "green";
                            progress = null;
                        }));
                    }
                    catch (Exception ex)
                    {
                        _dialogs.Add(progress);
                        Application.Current.Dispatcher.Invoke(new Action(() =>
                        {
                            progress.MessageText = $"下载 bing 壁纸过程出现异常: {ExceptionUtil.GetExceptionText(ex)}";
                            progress.BackColor = "red";
                            progress = null;
                        }));
                    }
                    finally
                    {
                        Application.Current.Dispatcher.Invoke(new Action(() =>
                        {
                            if (progress != null)
                                progress.Close();
                        }));
                    }

                    /*
                    Application.Current.Dispatcher.Invoke(new Action(() =>
                    {
                        MessageBox.Show(message);
                    }));
                    */
                }
            });
        }

        private void AddShortcut_Click(object sender, RoutedEventArgs e)
        {
            WpfClientInfo.AddShortcutToStartupGroup("dp2SSL-自助借还");
            MessageBox.Show("快捷方式添加成功");
        }

        private void RemoveShortcut_Click(object sender, RoutedEventArgs e)
        {
            WpfClientInfo.RemoveShortcutFromStartupGroup("dp2SSL-自助借还", true);
            MessageBox.Show("快捷方式删除成功");
        }
    }
}
