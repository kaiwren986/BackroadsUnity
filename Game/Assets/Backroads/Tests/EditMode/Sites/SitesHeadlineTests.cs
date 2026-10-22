using System.IO;
using System.Linq;
using System.Text;
using Backroads.Sites;
using Backroads.Sites.Editor;
using NUnit.Framework;

namespace Backroads.Tests.EditMode.Sites
{
    /// <summary>The M3 gates of the site program (card sites-S5, design §13): Sites.Validation and Sites.MatchesProfile.</summary>
    public partial class Sites
    {
        /// <summary>
        /// Sites.Validation, the M3 headline gate: two fresh bakes of the town (parcels, plans, grading, meshes, plates,
        /// props; nothing saved) and every check of design §13 on them (SiteValidation: plans complete, no overlaps, stalls
        /// inside their lots and reachable, ADA counts, entrances are baked cuts, aprons within 5 mm and grades listed, the
        /// terrain under the paving in lane W's bake, props clear, template credits, the two bakes equal and the committed
        /// bake and manifest current), plus the template credits of the fixture plans (the town has no template plans).
        /// Writes artifacts/sites/(town)/validation-report.txt.
        /// </summary>
        [Test, Category("Sites")]
        public void Validation()
        {
            var town = SiteParcels.Towns().First();
            var a = SiteBake.Build(town);
            var b = SiteBake.Build(town);
            var v = SiteValidation.Run(a, b);
            int templates = 0;
            foreach (var f in PlanFixtures.All())
                foreach (var p in SitePlanner.Plan(f.ctx).plans.Where(p => p.source == PlanSource.Template))
                {
                    templates++;
                    if (p.osmCredit == null || p.osmCredit.Length == 0) v.errors.Add($"fixture {f.name}: template {p.templateId} without its OSM ids");
                }
            v.lines.Add($"ok   fixture template plans credit their OSM sources: {templates}");
            var path = SiteValidation.WriteArtifacts(a, v);
            TestContext.WriteLine(SiteValidation.Report(a, v));
            TestContext.WriteLine("wrote " + path);
            Assert.IsEmpty(v.errors, $"{v.errors.Count} errors, first: {v.errors.FirstOrDefault()}");
        }

        /// <summary>
        /// Sites.MatchesProfile: the generated town measured as the study measured real towns (SiteProfileCheck: lot
        /// position, size, gaps, width and entrances per served type, the fuel canopy and shop, house driveways, garages,
        /// setbacks and frontage) against the blended profile's p10-p90 bands and shares; fails below 70 % inside. Writes
        /// artifacts/sites/(town)/profile-check.md.
        /// </summary>
        [Test, Category("Sites")]
        public void MatchesProfile()
        {
            var town = SiteParcels.Towns().First();
            var plans = SitePlans.BuildTown(town);
            var res = SiteProfileCheck.Run(plans, plans.ctx.profile);
            var table = SiteProfileCheck.Table(res);
            var dir = SiteParcels.ArtifactsDir(town);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "profile-check.md"), table.Replace("\r\n", "\n"), new UTF8Encoding(false));
            TestContext.WriteLine(table);
            Assert.Greater(res.counted, 10, "too few measures to judge");
            Assert.That(res.Score, Is.GreaterThanOrEqualTo(SiteProfileCheck.Pass), $"{res.inside} of {res.counted} measures inside the study's bands");
        }
    }
}
