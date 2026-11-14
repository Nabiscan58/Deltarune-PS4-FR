// DeltaruneFR-PS4: window around Port.csx. It asks for the folder of the PS4 files (copied over FTP), the folder of
// the PC game with the French patch, and an output folder, then runs utmt\UndertaleModCli.exe (UndertaleModTool, GPLv3)
// with Port.csx and shows its progress. Nothing is installed and nothing is sent anywhere.
// C# 5 / .NET Framework 4.5: mcs -sdk:4.5 -target:winexe -r:System.Windows.Forms.dll -r:System.Drawing.dll
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("DeltaruneFR-PS4")]
[assembly: System.Reflection.AssemblyProduct("DeltaruneFR-PS4")]

class MainForm : Form {
    readonly string root = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
    readonly TextBox ps4, pc, outp, log;
    readonly Button start;
    readonly Label step, elapsed;
    readonly ProgressBar bar;
    readonly ConcurrentQueue<string> lines = new ConcurrentQueue<string>();
    readonly Timer timer = new Timer { Interval = 200 };
    Process proc;
    DateTime t0;
    string result;

    TextBox Row(string label, int y, string value) {
        Controls.Add(new Label { Text = label, Location = new Point(12, y + 3), Size = new Size(250, 20) });
        var t = new TextBox { Location = new Point(265, y), Size = new Size(395, 22), Text = value, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
        var b = new Button { Text = "...", Location = new Point(666, y - 1), Size = new Size(34, 24), Anchor = AnchorStyles.Top | AnchorStyles.Right };
        b.Click += (s, e) => { using (var d = new FolderBrowserDialog { SelectedPath = t.Text }) if (d.ShowDialog() == DialogResult.OK) t.Text = d.SelectedPath; };
        Controls.Add(t); Controls.Add(b);
        return t;
    }

    public MainForm() {
        Text = "DeltaruneFR-PS4 0.6 - patch FR de DELTARUNE pour la version PS4";
        ClientSize = new Size(712, 520);
        MinimumSize = new Size(640, 420);
        StartPosition = FormStartPosition.CenterScreen;
        Controls.Add(new Label {
            Text = "Fabrique les fichiers PS4 en français à partir de TES fichiers : la copie FTP du jeu PS4 et ton jeu PC avec le patch FR. " +
                   "Le résultat contient le jeu : il est pour ta console, ne le partage pas.",
            Location = new Point(12, 10), Size = new Size(688, 34), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right });
        ps4 = Row("Fichiers PS4 (contient CUSAxxxxx-app0)", 52, Directory.Exists(@"C:\DeltarunePS4") ? @"C:\DeltarunePS4" : "");
        pc = Row("Jeu PC en français (contient DELTARUNE.exe)", 82, Directory.Exists(@"C:\DELTARUNE") ? @"C:\DELTARUNE" : "");
        outp = Row("Dossier de sortie", 112, Path.Combine(root, "sortie"));
        start = new Button { Text = "Démarrer", Location = new Point(12, 146), Size = new Size(120, 30) };
        start.Click += (s, e) => Start();
        Controls.Add(start);
        step = new Label { Location = new Point(144, 146), Size = new Size(430, 18), Font = new Font(Font, FontStyle.Bold), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
        elapsed = new Label { Location = new Point(580, 146), Size = new Size(120, 18), TextAlign = ContentAlignment.TopRight, Anchor = AnchorStyles.Top | AnchorStyles.Right };
        bar = new ProgressBar { Location = new Point(144, 166), Size = new Size(556, 10), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
        Controls.Add(step); Controls.Add(elapsed); Controls.Add(bar);
        log = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = new Font("Consolas", 8.5f),
                            Location = new Point(12, 186), Size = new Size(688, 322), Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right };
        Controls.Add(log);
        timer.Tick += (s, e) => Pump();
        FormClosing += (s, e) => {
            if (proc == null) return;
            if (MessageBox.Show("Le travail en cours sera arrêté. Fermer quand même ?", Text, MessageBoxButtons.YesNo) != DialogResult.Yes) { e.Cancel = true; return; }
            try { proc.Kill(); } catch (Exception) { }
        };
    }

    static readonly uint[] crcTable = MakeCrcTable();
    static uint[] MakeCrcTable() {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++) { uint c = n; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1; t[n] = c; }
        return t;
    }
    static uint Crc32(string path) {
        uint c = 0xFFFFFFFFu;
        var buf = new byte[1 << 16];
        using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) {
            int n;
            while ((n = f.Read(buf, 0, buf.Length)) > 0) for (int i = 0; i < n; i++) c = crcTable[(c ^ buf[i]) & 0xFF] ^ (c >> 8);
        }
        return c ^ 0xFFFFFFFFu;
    }

    /// <summary>Every file of the tool against fichiers.crc (crc32, size, path): a file damaged by the download or the
    /// extraction gives errors that say nothing of the cause.</summary>
    string CheckFiles() {
        string list = Path.Combine(root, "fichiers.crc");
        if (!File.Exists(list)) return "fichiers.crc manque";
        var bad = new StringBuilder();
        int count = 0;
        foreach (string line in File.ReadAllLines(list)) {
            string[] p = line.Split(new[] { ' ' }, 3);
            if (p.Length < 3) continue;
            string f = Path.Combine(root, p[2].Replace('/', Path.DirectorySeparatorChar));
            string why = null;
            try {
                if (!File.Exists(f)) why = "manque";
                else if (new FileInfo(f).Length != long.Parse(p[1])) why = "taille " + new FileInfo(f).Length + " au lieu de " + p[1];
                else if (Crc32(f).ToString("x8") != p[0]) why = "contenu endommagé";
            }
            catch (Exception e) { why = e.Message; }
            if (why != null && count++ < 12) bad.AppendLine("  " + p[2] + " : " + why);
        }
        return count == 0 ? null : count + " fichier(s) de l'outil endommagé(s) ou manquant(s) :\n" + bad;
    }

    void Start() {
        string cli = Path.Combine(root, "utmt", "UndertaleModCli.exe"), script = Path.Combine(root, "Port.csx");
        if (!File.Exists(cli) || !File.Exists(script)) { MessageBox.Show("utmt\\UndertaleModCli.exe ou Port.csx manque : décompresse le zip à nouveau, en entier.", Text); return; }
        string a = ps4.Text.Trim(), b = pc.Text.Trim(), c = outp.Text.Trim();
        if (!Directory.Exists(a)) { MessageBox.Show("Le dossier des fichiers PS4 n'existe pas.", Text); return; }
        if (!File.Exists(Path.Combine(b, "data.win"))) { MessageBox.Show("Le dossier du jeu PC doit contenir data.win (c'est celui de DELTARUNE.exe).", Text); return; }
        if (!File.Exists(Path.Combine(b, "data.win.bak"))) {
            MessageBox.Show("Pas de data.win.bak dans le dossier du jeu PC : ce sont les fichiers d'origine que l'installateur du patch FR garde, et l'outil en a besoin.", Text);
            return;
        }
        if (c == "") { MessageBox.Show("Choisis un dossier de sortie.", Text); return; }
        log.Clear();
        step.Text = "Vérification des fichiers de l'outil...";
        Application.DoEvents();
        string damaged = CheckFiles();
        if (damaged != null) {
            step.Text = "Fichiers de l'outil endommagés";
            log.Text = damaged.Replace("\n", "\r\n");
            MessageBox.Show(damaged + "\nLe zip a été mal téléchargé ou mal décompressé. Supprime ce dossier, décompresse le zip à nouveau (de préférence avec 7-Zip, " +
                            "qui signale les fichiers abîmés) et, si ça recommence, télécharge le zip à nouveau.", Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        result = null;
        var psi = new ProcessStartInfo(cli, "load \"" + Path.Combine(b, "data.win") + "\" -s \"" + script + "\"") {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = root };
        psi.EnvironmentVariables["DRFR_PS4"] = a;
        psi.EnvironmentVariables["DRFR_PC"] = b;
        psi.EnvironmentVariables["DRFR_OUT"] = c;
        try {
            proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.OutputDataReceived += (s, e) => { if (e.Data != null) lines.Enqueue(e.Data); };
            proc.ErrorDataReceived += (s, e) => { if (e.Data != null) lines.Enqueue(e.Data); };
            proc.Start();
            proc.StandardInput.Close();          // on an error the tool would wait for a key: with no input it stops instead
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
        }
        catch (Exception e) { proc = null; MessageBox.Show("Impossible de lancer l'outil : " + e.Message, Text); return; }
        t0 = DateTime.Now;
        start.Enabled = false;
        bar.Style = ProgressBarStyle.Marquee;
        step.Text = "Démarrage (lecture du script, une minute sur un PC lent)...";
        timer.Start();
    }

    void Pump() {
        var sb = new StringBuilder();
        string l;
        while (lines.TryDequeue(out l)) {
            if (l.StartsWith("@@step ")) { step.Text = "Étape : " + l.Substring(7); continue; }
            if (l.StartsWith("@@done ")) { result = l.Substring(7); continue; }
            sb.AppendLine(l);
        }
        if (sb.Length > 0) log.AppendText(sb.ToString());
        var e = DateTime.Now - t0;
        elapsed.Text = (int)e.TotalMinutes + " min " + e.Seconds.ToString("00") + " s";
        if (proc == null || !proc.HasExited) return;
        if (!lines.IsEmpty) return;              // one more turn for the last lines
        int code = proc.ExitCode;
        proc.Dispose();
        proc = null;
        timer.Stop();
        bar.Style = ProgressBarStyle.Blocks;
        start.Enabled = true;
        string journal = Path.Combine(outp.Text.Trim(), "deltarune-fr-ps4-journal.txt");
        if (result == null) {
            step.Text = "Arrêté sur une erreur";
            try { File.WriteAllText(Path.Combine(root, "deltarune-fr-ps4-erreur.txt"), log.Text, new UTF8Encoding(true)); } catch (Exception) { }
            MessageBox.Show("L'outil s'est arrêté sur une erreur (code " + code + ").\n\nLe détail est dans deltarune-fr-ps4-erreur.txt (à côté de ce programme) : joins-le si tu signales le problème" +
                            (File.Exists(journal) ? " et le journal " + journal : "") + ".", Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        string[] r = result.Split(' ');
        step.Text = "Terminé : " + r[0] + " fichiers de données sur " + r[1] + ", " + r[2] + " point(s) à regarder";
        MessageBox.Show("Terminé : " + r[0] + " fichiers de données sur " + r[1] + ".\n\n" +
                        (r[2] != "0" ? r[2] + " point(s) à regarder (lignes « !! » du journal).\n\n" : "") +
                        "Le journal :\n" + journal + "\n\nLa suite (copie sur la PS4) est dans LISEZ-MOI.txt.", Text, MessageBoxButtons.OK,
                        r[2] == "0" && r[0] == r[1] ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        try { Process.Start("explorer.exe", "/select,\"" + journal + "\""); } catch (Exception) { }
    }

    [STAThread]
    static void Main() {
        Application.EnableVisualStyles();
        Application.Run(new MainForm());
    }
}
