
namespace ScottPlot
{
    partial class FormHelp
    {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            lblTitle = new System.Windows.Forms.Label();
            lblMessage = new System.Windows.Forms.Label();
            SuspendLayout();
            // 
            // lblTitle
            // 
            lblTitle.AutoSize = true;
            lblTitle.Font = new Font("Segoe UI", 18F, System.Drawing.FontStyle.Regular, GraphicsUnit.Point, 0);
            lblTitle.Location = new Point(20, 14);
            lblTitle.Margin = new Padding(5, 0, 5, 0);
            lblTitle.Name = "lblTitle";
            lblTitle.Size = new Size(214, 32);
            lblTitle.TabIndex = 0;
            lblTitle.Text = "鼠标键盘操作提示";
            // 
            // lblMessage
            // 
            lblMessage.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            lblMessage.Font = new Font("Segoe UI", 11.25F, System.Drawing.FontStyle.Regular, GraphicsUnit.Point, 0);
            lblMessage.Location = new Point(20, 116);
            lblMessage.Margin = new Padding(5, 0, 5, 0);
            lblMessage.Name = "lblMessage";
            lblMessage.Size = new Size(350, 387);
            lblMessage.TabIndex = 2;
            lblMessage.Text = "help message...";
            // 
            // FormHelp
            // 
            AutoScaleDimensions = new SizeF(10F, 21F);
            AutoScaleMode = AutoScaleMode.Font;
            BackColor = System.Drawing.Color.FromArgb(64, 64, 64);
            ClientSize = new Size(390, 519);
            Controls.Add(lblMessage);
            Controls.Add(lblTitle);
            ForeColor = System.Drawing.Color.White;
            Margin = new Padding(5);
            Name = "FormHelp";
            Text = "帮助";
            ResumeLayout(false);
            PerformLayout();

        }

        #endregion

        private System.Windows.Forms.Label lblTitle;
        private System.Windows.Forms.Label lblMessage;
    }
}