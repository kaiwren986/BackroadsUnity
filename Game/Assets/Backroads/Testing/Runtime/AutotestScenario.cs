using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Backroads.Testing
{
    /// <summary>
    /// A player autotest kind that lives outside AutotestRunner, so each lane keeps its scenarios in its own folder
    /// (Testing/Runtime/Scenarios/<Area>/, see OWNERS.json). Any non-abstract class in this assembly implementing it
    /// is found at start-up; StreamingAssets/Autotest/(id).json picks it with "kind".
    /// </summary>
    public interface IAutotestScenario
    {
        /// <summary>The scenario JSON's "kind", for example "shots" or "bench-revcycle".</summary>
        string Kind { get; }

        IEnumerator Run(AutotestContext ctx);
    }

    /// <summary>What a scenario may use: its parameters, the output folder, and the runner's helpers.</summary>
    public sealed class AutotestContext
    {
        readonly AutotestRunner _runner;

        internal AutotestContext(AutotestRunner runner, AutotestRunner.Scenario scenario, string json, string outDir)
        {
            _runner = runner;
            Scenario = scenario;
            Json = json;
            OutDir = outDir;
        }

        public AutotestRunner.Scenario Scenario { get; }
        public string Id => Scenario.id;
        /// <summary>The raw scenario JSON, for scenario-specific fields: <c>ctx.Params&lt;MyParams&gt;()</c>.</summary>
        public string Json { get; }
        public string OutDir { get; }
        /// <summary>False in batchmode or with a null graphics device: no screenshots, no visual checks.</summary>
        public bool Renders => _runner.Renders;

        public T Params<T>() where T : new() => string.IsNullOrEmpty(Json) ? new T() : JsonUtility.FromJson<T>(Json);
        public void Fail(string why) => _runner.Fail(why);
        public void Note(string note) => _runner.Note(note);
        public void Evidence(string path) => _runner.AddEvidence(path);
        /// <summary>A number for result.json ("metrics"), for gates and reports.</summary>
        public void Metric(string name, double value) => _runner.Metric(name, value);
        public IEnumerator Wait(float seconds) => _runner.Wait(seconds);
        /// <summary>Writes (out)/(name).png and adds it to the evidence (and the contact sheet).</summary>
        public IEnumerator Screenshot(string name) => _runner.Screenshot(name);
    }

    static class AutotestScenarios
    {
        static Dictionary<string, Type> s_Kinds;

        public static IAutotestScenario Create(string kind)
        {
            s_Kinds ??= typeof(IAutotestScenario).Assembly.GetTypes()
                .Where(t => !t.IsAbstract && typeof(IAutotestScenario).IsAssignableFrom(t) && t.GetConstructor(Type.EmptyTypes) != null)
                .Select(t => (t, kind: ((IAutotestScenario)Activator.CreateInstance(t)).Kind))
                .ToDictionary(x => x.kind, x => x.t, StringComparer.OrdinalIgnoreCase);
            return s_Kinds.TryGetValue(kind ?? "", out var type) ? (IAutotestScenario)Activator.CreateInstance(type) : null;
        }
    }
}
