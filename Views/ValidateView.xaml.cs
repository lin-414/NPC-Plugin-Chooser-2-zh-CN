// Views/ValidateView.xaml.cs
using NPC_Plugin_Chooser_2.View_Models;
using ReactiveUI;
using Splat;

namespace NPC_Plugin_Chooser_2.Views
{
    public partial class ValidateView : ReactiveUserControl<VM_Validate>
    {
        public ValidateView()
        {
            InitializeComponent();

            // Resolve the singleton VM when hosted through ViewModelViewHost (which
            // instantiates the view via the Splat factory with no DataContext set) —
            // same pattern as the other tab views.
            if (this.DataContext == null)
            {
                ViewModel = Locator.Current.GetService<VM_Validate>();
                DataContext = ViewModel;
            }
        }
    }
}
