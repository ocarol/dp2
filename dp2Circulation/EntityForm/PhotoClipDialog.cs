﻿using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

using DigitalPlatform;
using DigitalPlatform.Drawing;

namespace dp2Circulation
{
    /// <summary>
    /// 使用 FaceCenter 摄像头的图片获取和剪裁对话框
    /// </summary>
    public partial class PhotoClipDialog : Form, ICameraClip
    {
        public string CurrentCamera { get; set; }

        /*
        string m_strCurrentCamera = "";

        public string CurrentCamera
        {
            get
            {
                return m_strCurrentCamera;
            }
            set
            {
                m_strCurrentCamera = value;
            }
        }
        */

        // private DigitalPlatform.Drawing.QrRecognitionControl qrRecognitionControl1;

        public PhotoClipDialog()
        {
            InitializeComponent();

            this.tabControl_main.Enabled = false;   // 刚开始的时候冻结

            /*
            this.toolStrip1.Enabled = true;
            this.tabControl_main.Enabled = true;
            */

            /*
            this.qrRecognitionControl1 = new DigitalPlatform.Drawing.QrRecognitionControl();
            this.qrRecognitionControl1.PhotoMode = true;

            // 
            // tabPage_camera
            // 
            this.panel1.Controls.Add(this.qrRecognitionControl1);
            // 
            // qrRecognitionControl1
            // 
            this.qrRecognitionControl1.Dock = System.Windows.Forms.DockStyle.Fill;
            this.qrRecognitionControl1.Location = new System.Drawing.Point(0, 0);
            this.qrRecognitionControl1.Name = "qrRecognitionControl1";
            this.qrRecognitionControl1.Size = new System.Drawing.Size(98, 202);
            this.qrRecognitionControl1.TabIndex = 0;
            this.qrRecognitionControl1.BackColor = Color.DarkGray; //System.Drawing.SystemColors.Window;

            this.qrRecognitionControl1.FirstImageFilled += new FirstImageFilledEventHandler(qrRecognitionControl1_FirstImageFilled);
            */
        }

        /*
        void qrRecognitionControl1_FirstImageFilled(object sender, FirstImageFilledEventArgs e)
        {
            // this.toolStripButton_getAndClose.Enabled = true;
            this.toolStrip1.Enabled = true;
            this.tabControl_main.Enabled = true;

            // this.qrRecognitionControl1.BeginInvoke(new Action<bool>(this.qrRecognitionControl1.DisplayImage), true);
            if (e.Error)
                this.qrRecognitionControl1.Image = null;
        }
        */

        static Bitmap BuildTextImage(string strText,
            Color color,
            float fFontSize = 64,
            int nWidth = 400)
        {
            // 文字图片
            return ArtText.BuildArtText(
                strText,
                "Microsoft YaHei",  // "Consolas", // 
                fFontSize,  // (float)16,
                FontStyle.Regular,  // .Bold,
                color,
                Color.Transparent,
                Color.Gray,
                ArtEffect.None,
                nWidth);
        }

        private void CameraClipDialog_Load(object sender, EventArgs e)
        {
            // this.qrRecognitionControl1.BeginInvoke(new Action<string>(this.qrRecognitionControl1.DisplayText), "test");
#if NO
            this.qrRecognitionControl1.Image = BuildTextImage("正在初始化摄像头，请稍候 ...",
                Color.Gray,
                64,
                2000);
#endif
            /*
            {
                // 2018/10/23
                if (this.qrRecognitionControl1.Image != null)
                    this.qrRecognitionControl1.Image.Dispose();

                this.qrRecognitionControl1.Image = BuildTextImage("正在初始化摄像头，请稍候 ...",
                    Color.Gray,
                    64,
                    2000);
            }
            */

            ShowPictureMessage("正在初始化摄像头，请稍候 ...");

            // this.qrRecognitionControl1.CurrentCamera = m_strCurrentCamera;

            tabControl_main_SelectedIndexChanged(this, e);

            BeginDisplayVideo();

        }

        private void CameraClipDialog_FormClosing(object sender, FormClosingEventArgs e)
        {
            // m_strCurrentCamera = this.qrRecognitionControl1.CurrentCamera;

            // this.qrRecognitionControl1.EndCatch();

            CancelDisplayVideo();
        }

        private void CameraClipDialog_FormClosed(object sender, FormClosedEventArgs e)
        {

        }

        /*
        delegate void Delegate_EndCatch();
        internal void EndCatch()
        {
            Delegate_EndCatch d = new Delegate_EndCatch(_endCatch);
            this.BeginInvoke(d);
        }

        void _endCatch()
        {
            this.qrRecognitionControl1.EndCatch();
        }
        */

        private void panel1_SizeChanged(object sender, EventArgs e)
        {
            /*
            if (qrRecognitionControl1 != null)  // 可能控件还没有创建 2014/10/14
                qrRecognitionControl1.PerformAutoScale();
            */
        }

        private void toolStripButton_shoot_Click(object sender, EventArgs e)
        {
            // 2017/12/26
            if (this.pictureBox1.Image == null)
            {
                MessageBox.Show(this, "this.pictureBox1.Image == null");
                return;
            }

            // this.qrRecognitionControl1.DisplayText("正在探测边沿 ...");
            Shoot();
            if (this.toolStripButton_autoDetectEdge.Checked)
                DetectEdge();
            this.DisplayImage(true);
        }

        /// <summary>
        /// 显示或隐藏图像
        /// </summary>
        /// <param name="bDisplay">显示或隐藏图像。如果为 true 表示显示图像，隐藏文字显示；否则隐藏图像，显示文字</param>
        public void DisplayImage(bool bDisplay = true)
        {
            this.pictureBox1.Visible = bDisplay;
        }

        bool _pointsInitialized = false;

        void Shoot()
        {
            Image temp = this.pictureBox1.Image;  // 注意，此处 temp 可能为 null，会导致下一句抛出异常

            // this.pictureBox_clip.Image = new Bitmap(temp);
            ImageUtil.SetImage(this.pictureBox_clip, new Bitmap(temp)); // 2012/12/28
            if (this._pointsInitialized == false)
            {
                this.pictureBox_clip.InitialPoints(temp);
                _pointsInitialized = true;
            }

            this.tabControl_main.SelectedTab = this.tabPage_clip;
        }

        void DetectEdge()
        {
            if (this.pictureBox_clip.Image == null)
                return;

            Cursor old_cursor = this.Cursor;
            this.Cursor = Cursors.WaitCursor;
            try
            {
                double angle = 0;
                Rectangle rect;
                using (Bitmap bitmap = new Bitmap(this.pictureBox_clip.Image))
                {
                    // this.pictureBox1.Image = ImageUtil.AforgeAutoCrop(bitmap);
                    DetectBorderParam param = new DetectBorderParam(bitmap);
                    bool bRet = AForgeImageUtil.GetSkewParam(bitmap,
                        param,
                        out angle,
                        out rect);
                    if (bRet == false)
                    {
                        MessageBox.Show(this, "探测边框失败");
                        return;
                    }
                }

#if NO
            using (Bitmap bitmap = new Bitmap(this.pictureBox1.Image))
            {
                this.pictureBox1.Image = ImageUtil.Apply(bitmap,
                    angle,
                    rect);
            }
#endif

                List<Point> points = this.pictureBox_clip.ToPoints((float)angle, rect);
                this.pictureBox_clip.SetPoints(points);
            }
            finally
            {
                this.Cursor = old_cursor;
            }
        }

        private void toolStripButton_clip_output_Click(object sender, EventArgs e)
        {
            if (this.pictureBox_clip.Image == null)
            {
                MessageBox.Show(this, "没有可以输出的图像");
                return;
            }

            using (Bitmap bitmap = new Bitmap(this.pictureBox_clip.Image))
            {
                this.Image = AForgeImageUtil.Clip(bitmap,
                    this.pictureBox_clip.GetCorners());
            }

            this.tabControl_main.SelectedTab = this.tabPage_result;
        }

        private void toolStripButton_clip_autoCorp_Click(object sender, EventArgs e)
        {
            DetectEdge();
        }

        private void toolStripButton_getAndClose_Click(object sender, EventArgs e)
        {
            if (this.tabControl_main.SelectedTab == this.tabPage_preview)
            {
                this.Image = this.pictureBox1.Image;
            }
            else if (this.tabControl_main.SelectedTab == this.tabPage_clip)
            {
                using (Bitmap bitmap = new Bitmap(this.pictureBox_clip.Image))
                {
                    this.Image = AForgeImageUtil.Clip(bitmap,
                        this.pictureBox_clip.GetCorners());
                }
            }

            this.DialogResult = System.Windows.Forms.DialogResult.OK;
            this.Close();
        }


        private void toolStripButton_cancel_Click(object sender, EventArgs e)
        {
            this.DialogResult = System.Windows.Forms.DialogResult.Cancel;
            this.Close();
        }

        // 结果图像。也就是经过剪裁的最终图像
        public Image Image
        {
            get
            {
                return this.pictureBox_result.Image;
            }
            set
            {
                ImageUtil.SetImage(this.pictureBox_result, value);  // 2016/12/28
                _resultRotateAngle = 0;
            }
        }

        // 拍摄后的原始图像。未经剪裁的图像
        public Image BackupImage
        {
            get
            {
                return this.pictureBox_clip.Image;
            }
            set
            {
                ImageUtil.SetImage(this.pictureBox_clip, value);    // 2016/12/28
            }
        }

        // 图像处理指令。由剪裁指令，和旋转指令组合而成
        public string ProcessCommand
        {
            get
            {
                StringBuilder text = new StringBuilder();
                text.Append(this.pictureBox_clip.ClipCommand);
                if (this.ResultRotateAngle != 0)
                {
                    if (text.Length > 0)
                        text.Append(";");
                    text.Append(string.Format("r:{0}", this.ResultRotateAngle));
                }
                return text.ToString();
            }
        }

        public ImageInfo ImageInfo
        {
            get
            {
                ImageInfo info = new ImageInfo();
                info.Image = this.Image;
                info.BackupImage = this.BackupImage;
                info.ProcessCommand = this.ProcessCommand;
                return info;
            }
        }

        int _resultRotateAngle = 0;

        // 结果图像相对原始图像曾转动过的角度
        public int ResultRotateAngle
        {
            get
            {
                return this._resultRotateAngle;
            }
        }

        private void toolStripButton_ratate_Click(object sender, EventArgs e)
        {
            if (this.tabControl_main.SelectedTab == this.tabPage_clip)
            {
                this.pictureBox_clip.RotateImage(RotateFlipType.Rotate90FlipNone);
            }
            if (this.tabControl_main.SelectedTab == this.tabPage_result)
            {
                Image image = this.pictureBox_result.Image;
                if (image != null)
                {
                    image.RotateFlip(RotateFlipType.Rotate90FlipNone);
                    ImageUtil.SetImage(this.pictureBox_result, image);  // 2016/12/28

                    _resultRotateAngle += 90;
                    if (_resultRotateAngle == 360)
                        _resultRotateAngle = 0;
                }
            }
        }

        private void tabControl_main_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (this.tabControl_main.SelectedTab == this.tabPage_preview)
            {
                this.toolStripButton_ratate.Enabled = false;
                this.toolStripButton_clip_autoCorp.Enabled = false;
                this.toolStripButton_clip_output.Enabled = false;
                this.toolStripButton_paste.Enabled = false;
                this.toolStripButton_selectAll.Enabled = false;
            }
            if (this.tabControl_main.SelectedTab == this.tabPage_clip)
            {
                this.toolStripButton_ratate.Enabled = true;
                this.toolStripButton_clip_autoCorp.Enabled = true;
                this.toolStripButton_clip_output.Enabled = true;
                this.toolStripButton_paste.Enabled = true;
                this.toolStripButton_selectAll.Enabled = true;
            }
            if (this.tabControl_main.SelectedTab == this.tabPage_result)
            {
                this.toolStripButton_ratate.Enabled = true;
                this.toolStripButton_clip_autoCorp.Enabled = false;
                this.toolStripButton_clip_output.Enabled = false;
                this.toolStripButton_paste.Enabled = true;
                this.toolStripButton_selectAll.Enabled = false;
            }
        }

        private void toolStripButton_copy_Click(object sender, EventArgs e)
        {
            string strError = "";
            if (this.tabControl_main.SelectedTab == this.tabPage_preview)
            {
                if (this.pictureBox1.Image == null)
                {
                    strError = "图像为空，无法复制";
                    goto ERROR1;
                }
                Clipboard.SetImage(this.pictureBox1.Image);
            }
            if (this.tabControl_main.SelectedTab == this.tabPage_clip)
            {
                if (this.pictureBox_clip.Image == null)
                {
                    strError = "图像为空，无法复制";
                    goto ERROR1;
                }
                Clipboard.SetImage(this.pictureBox_clip.Image);
            }
            if (this.tabControl_main.SelectedTab == this.tabPage_result)
            {
                if (this.pictureBox_result.Image == null)
                {
                    strError = "图像为空，无法复制";
                    goto ERROR1;
                }
                Clipboard.SetImage(this.pictureBox_result.Image);
            }
            return;
        ERROR1:
            MessageBox.Show(this, strError);
        }

        private void toolStripButton_paste_Click(object sender, EventArgs e)
        {
            string strError = "";

            // 从剪贴板中取得图像对象
            List<Image> images = ImageUtil.GetImagesFromClipboard(out strError);
            if (images == null)
            {
                strError += "。无法进行粘贴";
                goto ERROR1;
            }
            Image image = images[0];

            if (this.tabControl_main.SelectedTab == this.tabPage_clip)
            {
                ImageUtil.SetImage(this.pictureBox_clip, image);   // 2016/12/28
                if (this._pointsInitialized == false)
                {
                    this.pictureBox_clip.InitialPoints(image);
                    _pointsInitialized = true;
                }
            }
            if (this.tabControl_main.SelectedTab == this.tabPage_result)
            {
                this.Image = image;
            }

            return;
        ERROR1:
            MessageBox.Show(this, strError);
        }

        // 全选，剪裁区。等于不加剪裁
        private void toolStripButton_selectAll_Click(object sender, EventArgs e)
        {
            if (this.tabControl_main.SelectedTab == this.tabPage_clip)
            {
                this.pictureBox_clip.SelectAll();
            }
        }


        CancellationTokenSource _cancel = new CancellationTokenSource();

        Task _taskDisplayVideo = null;

        void CancelDisplayVideo()
        {
            if (_cancel != null)
            {
                _cancel.Cancel();
                _cancel.Dispose();
                _cancel = null;
            }
        }

        void BeginDisplayVideo()
        {
            CancelDisplayVideo();

            _cancel = new CancellationTokenSource();
            _taskDisplayVideo = Task.Run(() =>
            {
                try
                {
                    var result = DisplayVideo(Program.MainForm.FaceReaderUrl,
                        () =>
                        {
                            this.Invoke((Action)(() =>
                            {
                                this.toolStrip1.Enabled = true;
                                this.tabControl_main.Enabled = true;
                            }));
                        },
                        _cancel.Token);
                    if (_cancel != null && _cancel.IsCancellationRequested == false)
                        ShowPictureMessage(result.ToString());
                }
                catch (Exception ex)
                {
                    ShowPictureMessage(ex.Message);
                }
            });
        }

        void ShowPictureMessage(string text)
        {
            this.Invoke((Action)(() =>
            {
                this.pictureBox1.Image = BuildTextImage(text,
    Color.Gray,
    64,
    2000);
            }));
        }

        void ShowMessageBox(string text)
        {
            this.Invoke((Action)(() =>
            {
                MessageBox.Show(this, text);
            }));
        }

        delegate void delegate_firstFrameDisplayed();

        NormalResult DisplayVideo(string url,
            delegate_firstFrameDisplayed displayed,
            CancellationToken token)
        {
            MyForm.FaceChannel channel = MyForm.StartFaceChannel(
    Program.MainForm.FaceReaderUrl,
    out string strError);
            if (channel == null)
                return new NormalResult
                {
                    Value = -1,
                    ErrorInfo = strError
                };

            try
            {
                int i = 0;
                while (token.IsCancellationRequested == false)
                {
                    var result = channel.Object.GetImage("");
                    if (result.Value == -1)
                        return result;
                    using (MemoryStream stream = new MemoryStream(result.ImageData))
                    {
                        this.pictureBox1.Image = Image.FromStream(stream);
                    }

                    // 第一帧显示成功
                    if (i == 0)
                        displayed?.Invoke();

                    i++;
                }

                return new NormalResult();
            }
            catch (Exception ex)
            {
                strError = $"针对 {url} 的 GetImage() 请求失败: { ex.Message}";
                return new NormalResult
                {
                    Value = -1,
                    ErrorInfo = strError
                };
            }
            finally
            {
                MyForm.EndFaceChannel(channel);
            }
        }


    }
}
