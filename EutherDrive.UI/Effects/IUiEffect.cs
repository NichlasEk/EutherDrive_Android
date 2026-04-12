using System.Threading.Tasks;
using Avalonia.Controls;

namespace EutherDrive.UI.Effects;

public interface IUiEffect
{
    Task Run(Control root);
}
