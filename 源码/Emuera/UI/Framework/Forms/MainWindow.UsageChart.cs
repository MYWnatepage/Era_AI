using System.Drawing;
using System.Windows.Forms;
using MinorShift.Emuera.NativeAI;
using MinorShift.Emuera.NativeDisplay;

namespace MinorShift.Emuera.Forms;

internal sealed partial class MainWindow
{
    private UsageChartPage usageChart;
    public bool UsageChartActive => usageChart != null;
    public void ShowUsageChart(UsagePoint[] days)
    {
        if (usageChart != null) return;
        usageChart = new UsageChartPage(days, CloseUsageChart);
        richTextBox1.Clear();
        richTextBox1.Focus();
        mainPicBox.Invalidate();
    }
    private void CloseUsageChart()
    {
        usageChart = null;
        richTextBox1.Clear();
        richTextBox1.Focus();
        console.RefreshStrings(true);
        mainPicBox.Invalidate();
    }
    private bool DrawUsageChart(Graphics graphics)
    {
        if (usageChart == null) return false;
        usageChart.Draw(graphics, mainPicBox.ClientRectangle, richTextBox1.Font, DisplayRuntime.Current, Runtime.Config.Config.LineHeight);
        return true;
    }
    private bool UsageChartKey(Keys key)
    {
        if (usageChart == null) return false;
        if (key == Keys.Escape) { usageChart.Command(0); return true; }
        if (key == Keys.Enter)
        {
            string value = richTextBox1.Text;
            richTextBox1.Clear();
            usageChart.Submit(value);
            mainPicBox.Invalidate();
            return true;
        }
        if (key is Keys.Left or Keys.Right)
        {
            usageChart.Step(key == Keys.Left ? -1 : 1);
            mainPicBox.Invalidate();
            return true;
        }
        return false;
    }
}
