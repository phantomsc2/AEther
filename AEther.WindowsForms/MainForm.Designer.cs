namespace AEther.WindowsForms
{
    partial class MainForm
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
            this.Panel = new System.Windows.Forms.TableLayoutPanel();
            this.InputGroup = new System.Windows.Forms.GroupBox();
            this.Input = new System.Windows.Forms.ListBox();
            this.StatesGroup = new System.Windows.Forms.GroupBox();
            this.States = new System.Windows.Forms.ListBox();
            this.AnalyzerOptionsGroup = new System.Windows.Forms.GroupBox();
            this.AnalyzerOptions = new System.Windows.Forms.PropertyGrid();
            this.Panel.SuspendLayout();
            this.InputGroup.SuspendLayout();
            this.StatesGroup.SuspendLayout();
            this.AnalyzerOptionsGroup.SuspendLayout();
            this.SuspendLayout();
            // 
            // Panel
            // 
            this.Panel.ColumnCount = 1;
            this.Panel.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 100F));
            this.Panel.Controls.Add(this.InputGroup, 0, 0);
            this.Panel.Controls.Add(this.StatesGroup, 0, 1);
            this.Panel.Controls.Add(this.AnalyzerOptionsGroup, 0, 2);
            this.Panel.Dock = System.Windows.Forms.DockStyle.Left;
            this.Panel.Location = new System.Drawing.Point(0, 0);
            this.Panel.Name = "Panel";
            this.Panel.RowCount = 3;
            this.Panel.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Absolute, 60F));
            this.Panel.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Absolute, 120F));
            this.Panel.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 100F));
            this.Panel.Size = new System.Drawing.Size(220, 681);
            this.Panel.TabIndex = 0;
            this.Panel.Visible = false;
            // 
            // InputGroup
            // 
            this.InputGroup.Controls.Add(this.Input);
            this.InputGroup.Dock = System.Windows.Forms.DockStyle.Fill;
            this.InputGroup.Location = new System.Drawing.Point(3, 3);
            this.InputGroup.Name = "InputGroup";
            this.InputGroup.Size = new System.Drawing.Size(214, 54);
            this.InputGroup.TabIndex = 0;
            this.InputGroup.TabStop = false;
            this.InputGroup.Text = "Input";
            // 
            // Input
            // 
            this.Input.Dock = System.Windows.Forms.DockStyle.Fill;
            this.Input.FormattingEnabled = true;
            this.Input.ItemHeight = 15;
            this.Input.Location = new System.Drawing.Point(3, 19);
            this.Input.Name = "Input";
            this.Input.Size = new System.Drawing.Size(208, 32);
            this.Input.TabIndex = 3;
            this.Input.SelectedValueChanged += new System.EventHandler(this.Input_SelectedValueChanged);
            // 
            // StatesGroup
            // 
            this.StatesGroup.Controls.Add(this.States);
            this.StatesGroup.Dock = System.Windows.Forms.DockStyle.Fill;
            this.StatesGroup.Location = new System.Drawing.Point(3, 63);
            this.StatesGroup.Name = "StatesGroup";
            this.StatesGroup.Size = new System.Drawing.Size(214, 114);
            this.StatesGroup.TabIndex = 1;
            this.StatesGroup.TabStop = false;
            this.StatesGroup.Text = "Output";
            // 
            // States
            // 
            this.States.Dock = System.Windows.Forms.DockStyle.Fill;
            this.States.FormattingEnabled = true;
            this.States.ItemHeight = 15;
            this.States.Location = new System.Drawing.Point(3, 19);
            this.States.Name = "States";
            this.States.Size = new System.Drawing.Size(208, 92);
            this.States.TabIndex = 2;
            // 
            // AnalyzerOptionsGroup
            // 
            this.AnalyzerOptionsGroup.Controls.Add(this.AnalyzerOptions);
            this.AnalyzerOptionsGroup.Dock = System.Windows.Forms.DockStyle.Fill;
            this.AnalyzerOptionsGroup.Location = new System.Drawing.Point(3, 183);
            this.AnalyzerOptionsGroup.Name = "AnalyzerOptionsGroup";
            this.AnalyzerOptionsGroup.Size = new System.Drawing.Size(214, 495);
            this.AnalyzerOptionsGroup.TabIndex = 2;
            this.AnalyzerOptionsGroup.TabStop = false;
            this.AnalyzerOptionsGroup.Text = "Analyzer";
            // 
            // AnalyzerOptions
            // 
            this.AnalyzerOptions.Dock = System.Windows.Forms.DockStyle.Fill;
            this.AnalyzerOptions.HelpVisible = false;
            this.AnalyzerOptions.Location = new System.Drawing.Point(3, 19);
            this.AnalyzerOptions.Name = "AnalyzerOptions";
            this.AnalyzerOptions.Size = new System.Drawing.Size(208, 473);
            this.AnalyzerOptions.TabIndex = 1;
            this.AnalyzerOptions.ToolbarVisible = false;
            this.AnalyzerOptions.PropertyValueChanged += new System.Windows.Forms.PropertyValueChangedEventHandler(this.Options_PropertyValueChanged);
            // 
            // MainForm
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(7F, 15F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(1264, 681);
            this.Controls.Add(this.Panel);
            this.DoubleBuffered = true;
            this.Font = new System.Drawing.Font("Segoe UI", 9F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            this.KeyPreview = true;
            this.Name = "MainForm";
            this.Text = "AEther";
            this.Panel.ResumeLayout(false);
            this.InputGroup.ResumeLayout(false);
            this.StatesGroup.ResumeLayout(false);
            this.AnalyzerOptionsGroup.ResumeLayout(false);
            this.ResumeLayout(false);

        }

        #endregion

        private System.Windows.Forms.TableLayoutPanel Panel;
        private System.Windows.Forms.GroupBox InputGroup;
        private System.Windows.Forms.ListBox Input;
        private System.Windows.Forms.GroupBox StatesGroup;
        private System.Windows.Forms.ListBox States;
        private System.Windows.Forms.GroupBox AnalyzerOptionsGroup;
        private System.Windows.Forms.PropertyGrid AnalyzerOptions;
    }
}