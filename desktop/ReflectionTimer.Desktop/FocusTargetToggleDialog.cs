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
    internal FocusTargetToggleDialog(IReadOnlyList<FocusTarget> targets,AppColorTheme theme,Action<string> speak)
    {
        if(targets.Count==0||targets.Count>3)throw new ArgumentException("Choose an available browser target.");
        this.targets=targets;this.speak=speak;
        Text="Toggle a Focus target";AccessibleName=Text;AccessibleRole=AccessibleRole.Dialog;
        AccessibleDescription="Use Up and Down to select a target, then Enter. Number keys select the corresponding numbered choice. Escape cancels.";
        FormBorderStyle=FormBorderStyle.FixedDialog;ShowInTaskbar=true;MinimizeBox=false;MaximizeBox=false;
        StartPosition=FormStartPosition.CenterScreen;AutoScaleMode=AutoScaleMode.Dpi;TopMost=true;
        AutoSize=true;AutoSizeMode=AutoSizeMode.GrowAndShrink;
        Icon=Icon.ExtractAssociatedIcon(Environment.ProcessPath!)??SystemIcons.Question;
        var palette=PreviewTheme.Palette(theme,SystemInformation.HighContrast);BackColor=palette.Background;ForeColor=palette.Text;
        var instructions=new Label{Text="Select the target to add or remove.\nUp / Down, then Enter; or press its number. Escape cancels.",AutoSize=true,MaximumSize=new(490,0),Margin=new(0,0,0,12)};
        Choices=new(){Name="focus-shortcut-choices",AccessibleName="Target type",AccessibleDescription=AccessibleDescription,
            Width=490,IntegralHeight=false,TabIndex=0,BackColor=palette.Raised,ForeColor=palette.Text,HorizontalScrollbar=true};
        Choices.Height=Choices.ItemHeight*targets.Count+8;
        for(var i=0;i<targets.Count;i++)Choices.Items.Add($"{Number(targets[i])}. {FocusTargetToggle.Label(targets[i])}");
        Choices.SelectedIndex=0;
        Choices.SelectedIndexChanged+=(_,_)=>{if(Visible&&Choices.SelectedItem is {} choice)speak(choice.ToString()!);};
        Choices.DoubleClick+=(_,_)=>{if(Choices.SelectedIndex>=0)DialogResult=DialogResult.OK;};
        var confirm=new Button{Text="Toggle selected target",Name="focus-shortcut-confirm",AccessibleName="Toggle selected target",DialogResult=DialogResult.OK,
            AutoSize=true,MinimumSize=new(140,32),TabIndex=1,BackColor=palette.Raised,ForeColor=palette.Text,UseVisualStyleBackColor=false};
        var cancel=new Button{Text="Cancel",AccessibleName="Cancel",DialogResult=DialogResult.Cancel,AutoSize=true,MinimumSize=new(88,32),TabIndex=2,
            BackColor=palette.Raised,ForeColor=palette.Text,UseVisualStyleBackColor=false};
        ResetConfirmationDialog.ApplyFocusOutline(confirm,palette,ResetConfirmationDialog.FocusColor(palette,theme));
        ResetConfirmationDialog.ApplyFocusOutline(cancel,palette,ResetConfirmationDialog.FocusColor(palette,theme));
        var buttons=new FlowLayoutPanel{AutoSize=true,WrapContents=false,Margin=new(0,12,0,0)};buttons.Controls.Add(confirm);buttons.Controls.Add(cancel);
        var layout=new TableLayoutPanel{AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,ColumnCount=1,Padding=new(20),Dock=DockStyle.Fill};
        layout.Controls.Add(instructions);layout.Controls.Add(Choices);layout.Controls.Add(buttons);Controls.Add(layout);
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
    private static int Number(FocusTarget target)=>target.Kind==FocusTargetKind.BrowserTab?1:target.Kind==FocusTargetKind.BrowserTabGroup?2:3;
    protected override bool ProcessCmdKey(ref Message msg,Keys keyData)=>ChooseKey(keyData)||base.ProcessCmdKey(ref msg,keyData);
    protected override CreateParams CreateParams{get{var value=base.CreateParams;value.ExStyle=(value.ExStyle|0x40000)&~0x08000080;return value;}}
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);BeginInvoke(()=>{if(IsDisposed||!Visible)return;WindowActivation.Focus(this);ActiveControl=Choices;Choices.Focus();speak(Choices.SelectedItem!.ToString()!);});
    }
}
