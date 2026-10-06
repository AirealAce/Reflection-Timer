using ReflectionTimer.Core;
using ReflectionTimer.Desktop;

namespace ReflectionTimer.Accessible;

// Native list items expose their complete names and selection to screen readers.
// Optional app speech supplements the native control without a second UIA notice.
internal sealed class FocusTargetToggleDialog : Form
{
    private readonly IReadOnlyList<FocusTarget> targets;
    private readonly Action<string> speak;
    internal ListBox Choices { get; }
    internal FocusTarget? SelectedTarget=>Choices.SelectedIndex>=0?targets[Choices.SelectedIndex]:null;
    internal FocusTargetToggleDialog(IReadOnlyList<FocusTarget> targets,AppColorTheme theme,Func<FocusTarget,bool> isChecked,Action<string> speak)
    {
        if(targets.Count==0||targets.Count>3)throw new ArgumentException("Choose an available browser target.");
        this.targets=targets;this.speak=speak;
        Text="Toggle a Focus target";AccessibleName=Text;AccessibleRole=AccessibleRole.Dialog;
        AccessibleDescription="Use Up and Down to select a target, then Enter. Number keys select the corresponding numbered choice. Escape cancels.";
        FormBorderStyle=FormBorderStyle.FixedDialog;ShowInTaskbar=true;MinimizeBox=false;MaximizeBox=false;
        var textFont=new Font("Segoe UI",10.5f);Font=textFont;Disposed+=(_,_)=>textFont.Dispose();
        StartPosition=FormStartPosition.CenterScreen;AutoScaleDimensions=new(96,96);AutoScaleMode=AutoScaleMode.Dpi;TopMost=true;
        AutoSize=true;AutoSizeMode=AutoSizeMode.GrowAndShrink;
        Icon=Icon.ExtractAssociatedIcon(Environment.ProcessPath!)??SystemIcons.Question;
        var palette=PreviewTheme.Palette(theme,SystemInformation.HighContrast);BackColor=palette.Background;ForeColor=palette.Text;
        var instructions=new Label{Name="focus-shortcut-heading",Text="Choose the target to check or uncheck.",AutoSize=true,MaximumSize=new(480,0),Margin=new(0,0,0,12)};
        var checkedTargets=targets.Select(isChecked).ToArray();
        Choices=new(){Name="focus-shortcut-choices",AccessibleName="Target type",AccessibleDescription=AccessibleDescription,
            Width=480,IntegralHeight=false,TabIndex=0,BackColor=palette.Raised,ForeColor=palette.Text,HorizontalScrollbar=false,
            DrawMode=DrawMode.OwnerDrawFixed,BorderStyle=BorderStyle.FixedSingle,Dock=DockStyle.Fill,Margin=new(0,0,0,12)};
        for(var i=0;i<targets.Count;i++)Choices.Items.Add($"{Number(targets[i])}. {FocusTargetToggle.Label(targets[i])} ({(checkedTargets[i]?"checked":"unchecked")})");
        void SizeChoices(){
            Choices.ItemHeight=Math.Max(Choices.Font.Height*2+Choices.LogicalToDeviceUnits(20),Choices.LogicalToDeviceUnits(60));
            Choices.Height=Choices.ItemHeight*targets.Count+Choices.LogicalToDeviceUnits(4);
        }
        Choices.HandleCreated+=(_,_)=>SizeChoices();Choices.FontChanged+=(_,_)=>SizeChoices();Choices.DpiChangedAfterParent+=(_,_)=>SizeChoices();
        Choices.DrawItem+=(_,e)=>{
            if(e.Index<0||e.Index>=targets.Count)return;
            var selected=(e.State&DrawItemState.Selected)!=0;
            using var background=new SolidBrush(selected?palette.Selection:palette.Raised);e.Graphics.FillRectangle(background,e.Bounds);
            var inset=Choices.LogicalToDeviceUnits(10);var top=e.Bounds.Top+Choices.LogicalToDeviceUnits(8);var height=Choices.Font.Height;
            var state=checkedTargets[e.Index]?"Checked":"Unchecked";
            using var bold=new Font(Choices.Font,FontStyle.Bold);
            var stateWidth=TextRenderer.MeasureText(e.Graphics,"Unchecked",Choices.Font).Width;
            var titleWidth=e.Bounds.Width-inset*3-stateWidth;
            var kind=targets[e.Index].Kind switch{FocusTargetKind.Window=>"Window",FocusTargetKind.BrowserTab=>"Tab",_=>"Tab Group"};
            const TextFormatFlags flags=TextFormatFlags.NoPrefix|TextFormatFlags.SingleLine|TextFormatFlags.EndEllipsis|TextFormatFlags.VerticalCenter;
            TextRenderer.DrawText(e.Graphics,$"{Number(targets[e.Index])}. {kind}",bold,new Rectangle(e.Bounds.Left+inset,top,titleWidth,height),selected?palette.SelectionText:palette.Text,flags);
            TextRenderer.DrawText(e.Graphics,state,Choices.Font,new Rectangle(e.Bounds.Right-inset-stateWidth,top,stateWidth,height),
                selected?palette.SelectionText:checkedTargets[e.Index]?palette.Accent:palette.Muted,flags|TextFormatFlags.Right);
            var detail=targets[e.Index].Kind==FocusTargetKind.Window?targets[e.Index].App+" — "+targets[e.Index].Name:targets[e.Index].Name;
            TextRenderer.DrawText(e.Graphics,detail,Choices.Font,new Rectangle(e.Bounds.Left+inset,top+height+Choices.LogicalToDeviceUnits(4),e.Bounds.Width-inset*2,height),selected?palette.SelectionText:palette.Muted,flags);
            if((e.State&DrawItemState.Focus)!=0&&Choices.Focused){
                using var pen=new Pen(palette.Accent,Math.Max(2,Choices.LogicalToDeviceUnits(2))){Alignment=System.Drawing.Drawing2D.PenAlignment.Inset};
                e.Graphics.DrawRectangle(pen,e.Bounds.Left,e.Bounds.Top,e.Bounds.Width-1,e.Bounds.Height-1);
            }
        };
        SizeChoices();
        Choices.SelectedIndex=0;
        Choices.SelectedIndexChanged+=(_,_)=>{if(Visible&&Choices.SelectedItem is {} choice)speak(choice.ToString()!);};
        Choices.DoubleClick+=(_,_)=>{if(Choices.SelectedIndex>=0)DialogResult=DialogResult.OK;};
        var confirm=new Button{Text="Toggle selected target",Name="focus-shortcut-confirm",AccessibleName="Toggle selected target",DialogResult=DialogResult.OK,
            AutoSize=true,MinimumSize=new(144,36),Padding=new(10,4,10,4),Margin=Padding.Empty,TabIndex=1,BackColor=palette.Raised,ForeColor=palette.Text,UseVisualStyleBackColor=false};
        var cancel=new Button{Text="Cancel",AccessibleName="Cancel",DialogResult=DialogResult.Cancel,AutoSize=true,MinimumSize=new(88,36),Padding=new(10,4,10,4),Margin=new(8,0,0,0),TabIndex=2,
            BackColor=palette.Raised,ForeColor=palette.Text,UseVisualStyleBackColor=false};
        ResetConfirmationDialog.ApplyFocusOutline(confirm,palette,ResetConfirmationDialog.FocusColor(palette,theme));
        ResetConfirmationDialog.ApplyFocusOutline(cancel,palette,ResetConfirmationDialog.FocusColor(palette,theme));
        var keys=new Label{Name="focus-shortcut-keys",Text="Up/Down selects · Enter or number toggles · Esc cancels",ForeColor=palette.Muted,
            AutoSize=true,MaximumSize=new(480,0),Margin=new(0,0,0,16)};
        var buttons=new FlowLayoutPanel{Name="focus-shortcut-actions",AccessibleName="Target actions",AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,
            WrapContents=false,Anchor=AnchorStyles.Right,Margin=Padding.Empty,TabIndex=1};buttons.Controls.Add(confirm);buttons.Controls.Add(cancel);
        var layout=new TableLayoutPanel{AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,ColumnCount=1,RowCount=4,Padding=new(20),Dock=DockStyle.Fill,TabStop=false};
        layout.ColumnStyles.Add(new(SizeType.Percent,100));
        for(var i=0;i<4;i++)layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.Controls.Add(instructions,0,0);layout.Controls.Add(Choices,0,1);layout.Controls.Add(keys,0,2);layout.Controls.Add(buttons,0,3);Controls.Add(layout);
        AcceptButton=confirm;CancelButton=cancel;ActiveControl=Choices;
    }
    internal bool ChooseKey(Keys key)
    {
        if((key&Keys.Modifiers)!=Keys.None)return false;
        if(key is Keys.Up or Keys.Down){Choices.SelectedIndex=Math.Clamp(Choices.SelectedIndex+(key==Keys.Up?-1:1),0,targets.Count-1);Choices.Focus();return true;}
        var number=key>=Keys.D1&&key<=Keys.D3?(int)key-(int)Keys.D1+1:key>=Keys.NumPad1&&key<=Keys.NumPad3?(int)key-(int)Keys.NumPad1+1:-1;
        var index=Enumerable.Range(0,targets.Count).FirstOrDefault(i=>Number(targets[i])==number,-1);
        if(index>=0){Choices.SelectedIndex=index;DialogResult=DialogResult.OK;return true;}
        if(key==Keys.Enter&&Choices.SelectedIndex>=0){DialogResult=DialogResult.OK;return true;}
        return false;
    }
    private static int Number(FocusTarget target)=>target.Kind==FocusTargetKind.Window?1:target.Kind==FocusTargetKind.BrowserTab?2:3;
    protected override bool ProcessCmdKey(ref Message msg,Keys keyData)=>ChooseKey(keyData)||base.ProcessCmdKey(ref msg,keyData);
    protected override CreateParams CreateParams{get{var value=base.CreateParams;value.ExStyle=(value.ExStyle|0x40000)&~0x08000080;return value;}}
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);BeginInvoke(()=>{if(IsDisposed||!Visible)return;WindowActivation.Focus(this);ActiveControl=Choices;Choices.Focus();speak(Choices.SelectedItem!.ToString()!);});
    }
}
