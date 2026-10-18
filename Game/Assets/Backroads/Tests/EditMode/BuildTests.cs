using System.Collections.Generic;
using System.Linq;
using Backroads.Build.Editor;
using NUnit.Framework;

namespace Backroads.Tests.EditMode
{
    /// <summary>Backroads.Tests.EditMode.Build.*</summary>
    [TestFixture]
    public class Build
    {
        static IEnumerable<string> SettingNames() => ProjectSetup.Checks().Select(c => c.Name);

        /// <summary>Every PLAN 1.3 setting that ProjectSetup.Apply owns, one case each (T0.5).</summary>
        [TestCaseSource(nameof(SettingNames))]
        public void ProjectSettings(string setting)
        {
            var check = ProjectSetup.Checks().First(c => c.Name == setting);
            var problem = check.Problem();
            Assert.IsNull(problem, $"{setting}: {problem}. Run Backroads.Build.Editor.ProjectSetup.Apply.");
        }

        /// <summary>Architecture rule 8 [T15], on the newest builds\M(n)-server player (or BR_SERVER_BUILD).</summary>
        [Test]
        public void ServerContents()
        {
            var dir = Backroads.Build.Editor.ServerContents.FindLatestServerBuild();
            if (dir == null) Assert.Ignore("no ServerHeadless build under builds\\ (run BuildCli.Build -profile ServerHeadless, or set BR_SERVER_BUILD)");
            var problems = Backroads.Build.Editor.ServerContents.Check(dir);
            Assert.IsEmpty(problems, dir + ":\n" + string.Join("\n", problems));
        }
    }
}
