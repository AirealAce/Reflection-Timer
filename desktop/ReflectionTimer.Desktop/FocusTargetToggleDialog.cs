using ReflectionTimer.Core;
using ReflectionTimer.Desktop;

namespace ReflectionTimer.Accessible;

// Native checkboxes expose complete names, focus and checked state to readers.
// Optional app speech supplements their native focus announcements.
internal sealed class FocusTargetToggleDialog : Form
{
    private readonly IReadOnlyList<FocusTarget> targets;
    private int selectedIndex;
    private bool confirmed;
    private nint shortcutSourceWindow;
    private TaskCompletionSource<DialogResult>? shortcutCompletion;
    internal IReadOnlyList<CheckBox> Choices { get; }
    internal TableLayoutPanel ChoiceList { get; }
    internal FocusTarget? SelectedTarget=>targets[selectedIndex];
    internal FocusTargetToggleDialog(IReadOnlyList<FocusTarget> targets,AppColorTheme theme,Func<FocusTarget,bool> isChecked,Action<string> speak)
    {
        if(targets.Count==0||targets.Count>4)throw new ArgumentException("Choose an available browser target.");
        this.targets=targets;
        Text="Toggle a Focus target";AccessibleName=Text;AccessibleRole=AccessibleRole.Dialog;
        AccessibleDescription="Use Up and Down to focus a checkbox, then Enter or Space to toggle that target. Number keys toggle the corresponding numbered target. Escape cancels.";
        FormBorderStyle=FormBorderStyle.FixedDialog;ShowInTaskbar=true;MinimizeBox=false;MaximizeBox=false;
        var textFont=new Font("Segoe UI",10.5f);Font=textFont;Disposed+=(_,_)=>textFont.Dispose();
        StartPosition=FormStartPosition.CenterScreen;AutoScaleDimensions=new(96,96);AutoScaleMode=AutoScaleMode.Dpi;TopMost=true;
        AutoSize=true;AutoSizeMode=AutoSizeMode.GrowAndShrink;
        Icon=Icon.ExtractAssociatedIcon(Environment.ProcessPath!)??SystemIcons.Question;
        var palette=PreviewTheme.Palette(theme,SystemInformation.HighContrast);BackColor=palette.Background;ForeColor=palette.Text;
        var instructions=new Label{Name="focus-shortcut-heading",Text="Choose the target to check or uncheck.",AutoSize=true,MaximumSize=new(480,0),Margin=new(0,0,0,12)};
        ChoiceList=new(){Name="focus-shortcut-choices",AccessibleName="Target type",AccessibleRole=AccessibleRole.Grouping,
            AccessibleDescription=AccessibleDescription,Width=480,ColumnCount=1,RowCount=targets.Count,TabIndex=0,TabStop=false,
            BackColor=palette.Raised,ForeColor=palette.Text,BorderStyle=BorderStyle.FixedSingle,Dock=DockStyle.Fill,Margin=new(0,0,0,12)};
        ChoiceList.ColumnStyles.Add(new(SizeType.Percent,100));
        var choices=new List<CheckBox>();
        for(var i=0;i<targets.Count;i++){
            var index=i;var target=targets[i];
            var kind=target.Kind switch{FocusTargetKind.Window=>"Window",FocusTargetKind.BrowserTab=>"Tab",FocusTargetKind.BrowserTabGroup=>"Tab Group",_=>"Site"};
            var detail=target.Kind switch{FocusTargetKind.Window=>target.App+" — "+target.Name,FocusTargetKind.Site=>FocusSites.CanonicalHost(target.SiteHost),_=>target.Name};
            var choice=new CheckBox{Name="focus-shortcut-choice-"+Number(target),Text=$"{Number(target)}. {kind}\n{detail}",
                AccessibleName=$"{Number(target)}. {FocusTargetToggle.Label(target)}",AccessibleDescription=AccessibleDescription,
                Checked=isChecked(target),ThreeState=false,AutoEllipsis=true,UseMnemonic=false,
                Dock=DockStyle.Fill,Margin=Padding.Empty,Padding=new(10,8,10,8),TabIndex=i,
                BackColor=palette.Raised,ForeColor=palette.Text,UseVisualStyleBackColor=false};
            // Attach after setting the saved state. CheckedChanged also handles
            // UIA Toggle, which changes Checked without raising Click.
            choice.CheckedChanged+=(_,_)=>ConfirmChoice(index);
            choice.GotFocus+=(_,_)=>{
                selectedIndex=index;choice.BackColor=palette.Selection;choice.ForeColor=palette.SelectionText;choice.Invalidate();
                if(Visible)speak(choice.AccessibleName+(choice.Checked?" checked.":" unchecked."));
            };
            choice.LostFocus+=(_,_)=>{choice.BackColor=palette.Raised;choice.ForeColor=palette.Text;choice.Invalidate();};
            choice.Paint+=(_,e)=>{
                if(!choice.Focused)return;
                using var pen=new Pen(palette.Accent,Math.Max(2,choice.LogicalToDeviceUnits(2))){Alignment=System.Drawing.Drawing2D.PenAlignment.Inset};
                e.Graphics.DrawRectangle(pen,0,0,choice.ClientSize.Width-1,choice.ClientSize.Height-1);
            };
            choices.Add(choice);ChoiceList.RowStyles.Add(new(SizeType.Absolute,60));ChoiceList.Controls.Add(choice,0,i);
        }
        Choices=choices;
        void SizeChoices(){
            var height=Math.Max(Font.Height*2+ChoiceList.LogicalToDeviceUnits(20),ChoiceList.LogicalToDeviceUnits(60));
            foreach(RowStyle row in ChoiceList.RowStyles)row.Height=height;
            ChoiceList.Height=height*targets.Count+ChoiceList.LogicalToDeviceUnits(4);
        }
        ChoiceList.HandleCreated+=(_,_)=>SizeChoices();FontChanged+=(_,_)=>SizeChoices();ChoiceList.DpiChangedAfterParent+=(_,_)=>SizeChoices();SizeChoices();
        var confirm=new Button{Text="Toggle selected target",Name="focus-shortcut-confirm",AccessibleName="Toggle selected target",
            AutoSize=true,MinimumSize=new(144,36),Padding=new(10,4,10,4),Margin=Padding.Empty,TabIndex=0,BackColor=palette.Raised,ForeColor=palette.Text,UseVisualStyleBackColor=false};
        confirm.Click+=(_,_)=>ToggleChoice(selectedIndex);
        var cancel=new Button{Text="Cancel",AccessibleName="Cancel",DialogResult=DialogResult.Cancel,AutoSize=true,MinimumSize=new(88,36),Padding=new(10,4,10,4),Margin=new(8,0,0,0),TabIndex=1,
            BackColor=palette.Raised,ForeColor=palette.Text,UseVisualStyleBackColor=false};
        ResetConfirmationDialog.ApplyFocusOutline(confirm,palette,ResetConfirmationDialog.FocusColor(palette,theme));
        ResetConfirmationDialog.ApplyFocusOutline(cancel,palette,ResetConfirmationDialog.FocusColor(palette,theme));
        var keys=new Label{Name="focus-shortcut-keys",Text="Up/Down selects · Enter, Space or number toggles · Esc cancels",ForeColor=palette.Muted,
            AutoSize=true,MaximumSize=new(480,0),Margin=new(0,0,0,16)};
        var buttons=new FlowLayoutPanel{Name="focus-shortcut-actions",AccessibleName="Target actions",AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,
            WrapContents=false,Anchor=AnchorStyles.Right,Margin=Padding.Empty,TabIndex=1};buttons.Controls.Add(confirm);buttons.Controls.Add(cancel);
        var layout=new TableLayoutPanel{AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,ColumnCount=1,RowCount=4,Padding=new(20),Dock=DockStyle.Fill,TabStop=false};
        layout.ColumnStyles.Add(new(SizeType.Percent,100));
        for(var i=0;i<4;i++)layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.Controls.Add(instructions,0,0);layout.Controls.Add(ChoiceList,0,1);layout.Controls.Add(keys,0,2);layout.Controls.Add(buttons,0,3);Controls.Add(layout);
        AcceptButton=confirm;CancelButton=cancel;ActiveControl=Choices[0];
        cancel.Click+=(_,_)=>{DialogResult=DialogResult.Cancel;Close();};
        Disposed+=(_,_)=>shortcutCompletion?.TrySetResult(DialogResult==DialogResult.None?DialogResult.Cancel:DialogResult);
    }
    internal Task<DialogResult> ShowForShortcutAsync(nint sourceWindow)
    {
        if(IsDisposed||shortcutCompletion is not null)throw new InvalidOperationException("This target popup is no longer available.");
        shortcutSourceWindow=sourceWindow;
        shortcutCompletion=new(TaskCreationOptions.RunContinuationsAsynchronously);
        // ShowDialog restores the timer thread's last active window on exit,
        // even when this shortcut came from a browser. This independent popup
        // keeps timer viewers enabled and leaves their visibility/layout alone.
        try {
            Show();WindowActivation.Focus(this);
            if(WindowActivation.IsForeground(this)){ActiveControl=Choices[selectedIndex];Choices[selectedIndex].Focus();}
        }
        catch(Exception error){shortcutCompletion.TrySetException(error);Close();}
        return shortcutCompletion.Task;
    }
    private void ConfirmChoice(int index)
    {
        if(confirmed)return;
        confirmed=true;selectedIndex=index;
        foreach(var choice in Choices)choice.AutoCheck=false;
        DialogResult=DialogResult.OK;
        if(!Modal&&Visible)Close();
    }
    private void ToggleChoice(int index)
    {
        if(confirmed)return;
        Choices[index].Checked=!Choices[index].Checked;
    }
    internal bool ChooseKey(Keys key)
    {
        if((key&Keys.Modifiers)!=Keys.None)return false;
        if(key is Keys.Up or Keys.Down){selectedIndex=Math.Clamp(selectedIndex+(key==Keys.Up?-1:1),0,targets.Count-1);ActiveControl=Choices[selectedIndex];Choices[selectedIndex].Focus();return true;}
        var number=key>=Keys.D1&&key<=Keys.D4?(int)key-(int)Keys.D1+1:key>=Keys.NumPad1&&key<=Keys.NumPad4?(int)key-(int)Keys.NumPad1+1:-1;
        var index=Enumerable.Range(0,targets.Count).FirstOrDefault(i=>Number(targets[i])==number,-1);
        if(index>=0){ToggleChoice(index);return true;}
        if(key is Keys.Enter or Keys.Space){ToggleChoice(selectedIndex);return true;}
        return false;
    }
    private static int Number(FocusTarget target)=>target.Kind switch{FocusTargetKind.Window=>1,FocusTargetKind.BrowserTab=>2,FocusTargetKind.BrowserTabGroup=>3,_=>4};
    protected override bool ProcessCmdKey(ref Message msg,Keys keyData)
    {
        if((keyData is Keys.Enter or Keys.Space)&&ActiveControl is Button)return base.ProcessCmdKey(ref msg,keyData);
        return ChooseKey(keyData)||base.ProcessCmdKey(ref msg,keyData);
    }
    protected override CreateParams CreateParams{get{var value=base.CreateParams;value.ExStyle=(value.ExStyle|0x40000)&~0x08000080;return value;}}
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if(e.Cancel)return;
        if(DialogResult==DialogResult.None)DialogResult=DialogResult.Cancel;
        // Restore before hiding while the popup still owns foreground focus.
        // If the user switched away, or the source closed, leave focus alone.
        if(shortcutCompletion is not null)WindowActivation.ReleaseFocus(this,shortcutSourceWindow,window=>window==shortcutSourceWindow);
    }
    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);
        shortcutCompletion?.TrySetResult(DialogResult);
    }
}
