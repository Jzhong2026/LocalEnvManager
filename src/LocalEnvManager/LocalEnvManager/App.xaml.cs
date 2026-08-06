using System.Windows;

namespace LocalEnvManager
{
	public partial class App : Application
	{
		private readonly AppBootstrapper _bootstrapper;

		public App()
		{
			InitializeComponent();
			_bootstrapper = new AppBootstrapper();
		}
	}

}
