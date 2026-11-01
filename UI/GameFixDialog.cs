using Atlas.Core;
using Atlas.Native;

namespace Atlas.UI;

/// <summary>"Atlas cannot see the frames of X — fix?" with what exactly will change, then the result.</summary>
sealed class GameFixDialog : Form
{
    readonly string _exe;
    readonly Label _body = Theme.Label("", Theme.Body, Theme.Muted);
    readonly FlatButton _fix = new("Исправить", primary: true) { Size = new Size(170, 40) };
    readonly FlatButton _later = new("Не сейчас") { Size = new Size(140, 40) };

    public bool Fixed { get; private set; }

    /// <summary>Off-screen rendering for tests: never activates.</summary>
    public bool Preview { get; init; }
    protected override bool ShowWithoutActivation => Preview;

    public GameFixDialog(string game, string exe)
    {
        _exe = exe;
        Text = "Atlas — запись игры";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        BackColor = Theme.Card;
        ForeColor = Theme.Text;
        Font = Theme.Body;
        ClientSize = new Size(560, 370);
        Icon = Theme.IconFrom(Theme.Logo(32));

        var title = Theme.Label($"Atlas не видит кадры «{game}»", Theme.H2);
        title.Location = new Point(28, 24);
        title.MaximumSize = new Size(500, 0);
        bool nv = NvidiaProfiles.Available;
        _body.Text =
            "Похоже, игра работает в эксклюзивном полноэкранном режиме — её кадры идут мимо рабочего стола, " +
            "и захват Windows их не получает.\n\n" +
            "Atlas может это исправить, не трогая саму игру (для античита ничего не меняется):\n" +
            (nv ? "  •  в драйвере NVIDIA для этой игры: «Способ представления OpenGL/Vulkan → через DXGI»\n" : "") +
            "  •  в Windows: включить «оптимизацию во весь экран» для этой игры\n\n" +
            "После этого перезапусти игру. Отменить можно в Настройки → Поведение." +
            (nv ? "" : "\n\nЕсли не поможет — включи в игре режим «Оконный без рамки».");
        _body.Location = new Point(28, 64);
        _body.MaximumSize = new Size(504, 0);

        _fix.Location = new Point(ClientSize.Width - 28 - _fix.Width - _later.Width - 12, ClientSize.Height - 64);
        _later.Location = new Point(ClientSize.Width - 28 - _later.Width, ClientSize.Height - 64);
        _fix.Click += (_, _) => DoFix();
        _later.Click += (_, _) => Close();
        Controls.AddRange(new Control[] { title, _body, _fix, _later });
    }

    void DoFix()
    {
        _fix.Enabled = false;
        _fix.Text = "Применяю…";
        Application.DoEvents();
        var r = GameFix.Apply(_exe);
        Fixed = r.Ok;
        _body.ForeColor = r.Ok ? Theme.Text : Theme.Bad;
        _body.Text = r.Ok
            ? "Готово!\n\n" +
              (r.NvidiaApplied ? "✓ Драйвер NVIDIA: игра выводит кадры через DXGI\n" : "") +
              "✓ Оптимизация во весь экран для игры включена\n\n" +
              "Перезапусти игру — после этого Atlas будет её записывать даже в полноэкранном режиме."
            : $"Не получилось: {r.Error}.\n\nМожно включить в игре режим «Оконный без рамки» — это работает всегда.";
        _fix.Visible = false;
        _later.Text = "Готово";
        _later.Fill = Theme.Accent;
        _later.FillHover = Theme.AccentHover;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int on = 1;
        DwmSetWindowAttribute(Handle, 20, ref on, 4);
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
