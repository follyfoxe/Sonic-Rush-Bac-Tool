using RushBacLib;
using System;
using System.Windows.Forms;

namespace RushBacTool
{
    public partial class AnimFrameControl : UserControl
    {
        public int AnimIndex { get; }
        public AnimationFrame Animation { get; }

        readonly MainForm mainForm;

        int currentFrame;
        bool disposed;

        public AnimFrameControl()
        {
            InitializeComponent();
            Disposed += AnimFrameControl_Disposed;
            Dock = DockStyle.Fill;
        }

        void AnimFrameControl_Disposed(object sender, EventArgs e)
        {
            Stop();
            animTimer.Dispose();
            disposed = true;
        }

        public AnimFrameControl(MainForm form, int animIndex) : this()
        {
            mainForm = form;
            AnimIndex = animIndex;

            Animation = form.bacFile.AnimationFrames[animIndex];
            frameUpDown.Maximum = Animation.frames.Count - 1;

            titleLabel.Text += " " + animIndex;

            UpdatePreview();
        }

        void UpdatePreview()
        {
            if (!disposed)
                previewBox.Image = mainForm.bitmaps[AnimIndex][currentFrame];
        }

        void FrameUpDown_ValueChanged(object sender, EventArgs e)
        {
            currentFrame = (int)frameUpDown.Value;
            UpdatePreview();
        }

        void AnimTimer_Tick(object sender, EventArgs e)
        {
            if (currentFrame < Animation.frames.Count - 1)
                currentFrame++;
            else
                currentFrame = 0;

            frameUpDown.Value = currentFrame;
        }

        void Play()
        {
            currentFrame = 0;
            animTimer.Start();
        }

        void Stop()
        {
            animTimer.Stop();
        }

        void PlayButton_Click(object sender, EventArgs e) => Play();
        void StopButton_Click(object sender, EventArgs e) => Stop();
    }
}