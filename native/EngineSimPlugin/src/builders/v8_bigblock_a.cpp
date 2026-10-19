// v8_bigblock_a: a 7.4 L big-block V8 (4.25 in bore, 4.0 in stroke), the line-by-line C++ port of upstream's gas
// big-block truck example script (vendor/engine-sim/assets/engines/<maker>/<truck 454>.mr; the real names stay in the
// vendor tree, PLAN 3.10 [C2]) and the part-library nodes it uses (es/part-library/parts/{intakes,heads,camshafts,
// cam_lobes,ignition_modules}.mr, es/sound-library/impulse_responses.mr default_0). Defaults come from
// es/objects/objects.mr; the order of initialize() calls mirrors es_script::EngineNode::buildEngine(). The script has
// no main(), vehicle or transmission node, so, as for the script, the Instance supplies upstream's default vehicle.
// Truck engine of PLAN 3.10 (U6: the box truck runs a gas big-block V8). `esp_cli compare` checks the port against the
// script's own dyno sweep (ctest esp_bigblock).
#include "builders.h"
#include "builder_util.h"

#include "direct_throttle_linkage.h"
#include "engine.h"
#include "impulse_response.h"
#include "standard_valvetrain.h"

namespace esp {

using namespace build;

namespace {

struct CylinderSpec {
    int rodJournal;
    double blowbyScfm;
    double primaryLength;     // header primary, m
    double soundAttenuation;
    double firingDeg;         // ignition_modules.mr big-block distributor: wire w fires at (slot / 8) x 720 deg
};

} // namespace

bool buildV8BigblockA(const std::string &esDir, EngineBundle &out) {
    Engine *engine = new Engine;

    DirectThrottleLinkage *throttle = new DirectThrottleLinkage;
    DirectThrottleLinkage::Parameters throttleParams;
    throttleParams.gamma = 1.5;  // engine(throttle_gamma: 1.5)
    throttle->initialize(throttleParams);

    Engine::Parameters ep;
    ep.name = "v8_bigblock_a";
    ep.starterTorque = units::torque(200, units::ft_lb);
    ep.starterSpeed = units::rpm(200);  // engine_parameters default
    ep.redline = units::rpm(5500);
    ep.dynoMinSpeed = units::rpm(1000);
    ep.dynoMaxSpeed = ep.redline;
    ep.dynoHoldStep = units::rpm(100);
    ep.throttle = throttle;
    ep.initialSimulationFrequency = 10000;  // engine node default
    ep.initialHighFrequencyGain = 0.01;
    ep.initialNoise = 1.0;
    ep.initialJitter = 0.5;
    ep.crankshaftCount = 1;
    ep.cylinderBanks = 2;
    ep.cylinderCount = 8;
    ep.exhaustSystemCount = 2;
    ep.intakeCount = 1;
    engine->initialize(ep);

    // ---- crankshaft: four rod journals, two rods each (a cross-plane crank) ----
    Crankshaft *c0 = engine->getCrankshaft(0);
    Crankshaft::Parameters cp;
    cp.crankThrow = 2.0 * units::inch;
    cp.flywheelMass = 29 * 2 * units::lb;
    cp.mass = 75 * units::lb;
    cp.frictionTorque = 10.0 * units::ft_lb;
    cp.momentOfInertia = 0.22986844776863666 * 2;
    cp.pos_x = 0.0;
    cp.pos_y = 0.0;
    cp.tdc = constants::pi / 4;
    cp.rodJournals = 4;
    c0->initialize(cp);
    c0->setRodJournalAngle(0, 0.0);
    c0->setRodJournalAngle(1, -constants::pi / 2);
    c0->setRodJournalAngle(2, -3.0 * constants::pi / 2);
    c0->setRodJournalAngle(3, constants::pi);

    // ---- lobes and ports (cam_lobes.mr stock lobes, heads.mr peanut-port head) ----
    Function *intakeLobe = harmonicCamLobe(194 * units::deg, 0.8, 390 * units::thou, 100);
    Function *exhaustLobe = harmonicCamLobe(202 * units::deg, 0.8, 409 * units::thou, 100);

    const Samples intakeFlowCfm = {{0, 0},     {50, 25},   {100, 75},  {150, 100}, {200, 130},
                                   {250, 180}, {300, 190}, {350, 220}, {400, 240}, {450, 250},
                                   {500, 260}, {550, 260}, {600, 260}, {650, 255}, {700, 250}};
    const Samples exhaustFlowCfm = {{0, 0},     {50, 25},   {100, 50},  {150, 75},  {200, 100},
                                    {250, 125}, {300, 160}, {350, 175}, {400, 180}, {450, 190},
                                    {500, 200}, {550, 205}, {600, 210}, {650, 210}, {700, 210}};

    // ---- intake: intakes.mr stock big-block intake (650 cfm carburettor), shared by all eight cylinders ----
    Intake *intake = engine->getIntake(0);
    Intake::Parameters ip;
    ip.volume = 2.0 * units::L;
    ip.CrossSectionArea = 100.0 * units::cm2;
    ip.InputFlowK = GasSystem::k_carb(650.0);
    ip.IdleFlowK = GasSystem::k_carb(0.007);
    ip.RunnerFlowRate = GasSystem::k_carb(300.0);
    ip.MolecularAfr = 25.0 / 2.0;
    ip.IdleThrottlePlatePosition = 0.991;
    ip.RunnerLength = 6.0 * units::inch;
    ip.VelocityDecay = 1.0;

    // ---- exhausts: one per bank, the left one 72 in longer; impulse_responses.mr default_0 ----
    ExhaustSystem::Parameters xp;
    xp.collectorCrossSectionArea = constants::pi * (2.0 * units::inch) * (2.0 * units::inch);  // circle_area(2 in)
    xp.outletFlowRate = GasSystem::k_carb(550.0);
    xp.primaryTubeLength = 10.0 * units::inch;
    xp.primaryFlowRate = GasSystem::k_carb(100.0);
    xp.velocityDecay = 1.0;
    xp.audioVolume = 5.5;
    const double exhaustLength[2] = {(180 + 72.0) * units::inch, 180.0 * units::inch};

    const double distance = 5.0 * units::inch;
    // bank 0 (-45 deg): wires 1, 3, 5, 7; bank 1 (+45 deg): wires 2, 4, 6, 8. Firing order 1-8-4-3-6-5-7-2.
    const CylinderSpec cylinders[8] = {
        {0, 0.2, distance * 4, 0.9, 0.0},     // wire 1, slot 0
        {1, 0.6, distance * 3, 1.1, 270.0},   // wire 3, slot 3
        {2, 0.6, distance * 2, 0.8, 450.0},   // wire 5, slot 5
        {3, 0.4, distance * 1, 0.85, 540.0},  // wire 7, slot 6
        {0, 0.2, distance * 4, 0.9, 630.0},   // wire 2, slot 7
        {1, 0.2, distance * 3, 1.1, 180.0},   // wire 4, slot 2
        {2, 0.6, distance * 2, 0.8, 360.0},   // wire 6, slot 4
        {3, 0.6, distance * 1, 0.75, 90.0},   // wire 8, slot 1
    };

    const double pistonMass = 880 * units::g;
    const double compressionHeight = 1.640 * units::inch;
    const double rodMass = 785.0 * units::g;
    const double rodLength = 6.135 * units::inch;

    int global = 0;
    for (int b = 0; b < 2; ++b) {
        CylinderBank *bank = engine->getCylinderBank(b);
        CylinderBank::Parameters bp;
        bp.bore = 4.25 * units::inch;
        bp.deckHeight = 9.8 * units::inch;
        bp.positionX = 0.0;
        bp.positionY = 0.0;
        bp.displayDepth = 0.5;
        bp.angle = (b == 0 ? -45.0 : 45.0) * units::deg;
        bp.cylinderCount = 4;
        bp.index = b;
        bp.crankshaft = c0;
        bank->initialize(bp);

        const int firstGlobal = global;
        for (int i = 0; i < 4; ++i, ++global) {
            const CylinderSpec &spec = cylinders[global];
            Piston *piston = engine->getPiston(global);
            ConnectingRod *rod = engine->getConnectingRod(global);

            Piston::Parameters pp;
            pp.mass = pistonMass;
            pp.CompressionHeight = compressionHeight;
            pp.WristPinPosition = 0.0;
            pp.Displacement = 0.0;
            pp.BlowbyFlowCoefficient = GasSystem::k_28inH2O(spec.blowbyScfm);
            pp.Bank = bank;
            pp.CylinderIndex = i;
            pp.Rod = rod;
            piston->initialize(pp);

            ConnectingRod::Parameters rp;
            rp.mass = rodMass;
            rp.momentOfInertia = 0.0015884918028487504;
            rp.centerOfMass = 0.0;
            rp.length = rodLength;
            rp.slaveThrow = 0.0;
            rp.rodJournals = 0;
            rp.crankshaft = c0;
            rp.journal = spec.rodJournal;
            rp.piston = piston;
            rp.master = nullptr;
            rod->initialize(rp);
        }

        // camshafts.mr: the big-block cam builder with the stock lobes, intake centre 108 deg, exhaust centre 113 deg
        Camshaft *intakeCam = new Camshaft;
        Camshaft *exhaustCam = new Camshaft;
        Camshaft::Parameters camp;
        camp.advance = 0.0;
        camp.baseRadius = 0.75 * units::inch;
        camp.crankshaft = c0;
        camp.lobes = 4;
        camp.lobeProfile = intakeLobe;
        intakeCam->initialize(camp);
        camp.lobeProfile = exhaustLobe;
        exhaustCam->initialize(camp);

        const double rot90 = 90 * units::deg;
        const double rot360 = 360 * units::deg;
        const double intakeCenter = 108 * units::deg;
        const double exhaustCenter = 113 * units::deg;
        static const int kLobeQuarters[2][4] = {{0, 3, 5, 6}, {7, 2, 4, 1}};
        for (int lobe = 0; lobe < 4; ++lobe) {
            const double offset = kLobeQuarters[b][lobe] * rot90;
            exhaustCam->setLobeCenterline(lobe, rot360 - exhaustCenter + offset);
            intakeCam->setLobeCenterline(lobe, rot360 + intakeCenter + offset);
        }

        StandardValvetrain *valvetrain = new StandardValvetrain;
        StandardValvetrain::Parameters vp;
        vp.intakeCamshaft = intakeCam;
        vp.exhaustCamshaft = exhaustCam;
        valvetrain->initialize(vp);

        CylinderHead *head = engine->getHead(b);
        CylinderHead::Parameters hp;
        hp.Bank = bank;
        hp.Valvetrain = valvetrain;
        hp.IntakePortFlow = makeFlowFunction(intakeFlowCfm);
        hp.ExhaustPortFlow = makeFlowFunction(exhaustFlowCfm);
        hp.CombustionChamberVolume = 118.0 * units::cc;
        hp.IntakeRunnerVolume = 189.0 * units::cc;
        hp.IntakeRunnerCrossSectionArea = 37.8 * units::cm2;
        hp.ExhaustRunnerVolume = 300.0 * units::cc;                                        // cylinder_head default
        hp.ExhaustRunnerCrossSectionArea = constants::pi * (0.85 * units::inch) * (0.85 * units::inch);  // default
        hp.FlipDisplay = (b == 1);
        head->initialize(hp);

        ImpulseResponse *ir = new ImpulseResponse;
        ir->initialize(esDir + "/sound-library/smooth/smooth_39.wav", 0.001);
        ExhaustSystem *exhaust = engine->getExhaustSystem(b);
        ExhaustSystem::Parameters bx = xp;
        bx.length = exhaustLength[b];
        bx.impulseResponse = ir;
        for (int i = 0; i < 4; ++i) {
            const CylinderSpec &spec = cylinders[firstGlobal + i];
            exhaust->initialize(bx);  // the script node re-initialises the shared object per cylinder too
            intake->initialize(ip);
            head->setIntake(i, intake);
            head->setExhaustSystem(i, exhaust);
            head->setSoundAttenuation(i, spec.soundAttenuation);
            head->setHeaderPrimaryLength(i, spec.primaryLength);
        }
    }

    // ---- ignition: ignition_modules.mr distributor; rev limit 7,000 rpm, limiter_duration default 0.5 s ----
    Function *timing = makeFunction(units::rpm(1000), {{units::rpm(0), 12 * units::deg},
                                                       {units::rpm(1000), 12 * units::deg},
                                                       {units::rpm(2000), 20 * units::deg},
                                                       {units::rpm(3000), 30 * units::deg},
                                                       {units::rpm(4000), 38 * units::deg},
                                                       {units::rpm(5000), 38 * units::deg},
                                                       {units::rpm(6000), 38 * units::deg}});
    IgnitionModule::Parameters ignp;
    ignp.crankshaft = c0;
    ignp.cylinderCount = 8;
    ignp.revLimit = units::rpm(7000);
    ignp.timingCurve = timing;
    ignp.limiterDuration = 0.5 * units::sec;
    engine->getIgnitionModule()->initialize(ignp);
    for (int i = 0; i < 8; ++i) engine->getIgnitionModule()->setFiringOrder(i, cylinders[i].firingDeg * units::deg);

    Function *meanPistonSpeedToTurbulence = new Function;
    meanPistonSpeedToTurbulence->initialize(30, 1);
    for (int i = 0; i < 30; ++i) {
        const double s = static_cast<double>(i);
        meanPistonSpeedToTurbulence->addSample(s, s * 0.5);
    }

    Fuel::Parameters fp;  // objects.mr fuel{} defaults, with the script's three overrides
    fp.name = "gasoline";
    fp.molecularMass = 100 * units::g;
    fp.energyDensity = 48.1 * units::kJ / units::g;
    fp.density = 0.755 * units::kg / units::L;
    fp.molecularAfr = 25 / 2.0;
    fp.maxBurningEfficiency = 0.85;
    fp.burningEfficiencyRandomness = 0.5;
    fp.lowEfficiencyAttenuation = 0.6;
    fp.maxTurbulenceEffect = 3.0;
    fp.maxDilutionEffect = 10.0;
    fp.turbulenceToFlameSpeedRatio = makeFunction(5.0, {{0.0, 3.0},
                                                        {5.0, 1.5 * 5.0},
                                                        {10.0, 1.5 * 10.0},
                                                        {15.0, 1.5 * 15.0},
                                                        {20.0, 1.5 * 20.0},
                                                        {25.0, 1.5 * 25.0},
                                                        {30.0, 1.5 * 30.0},
                                                        {35.0, 1.5 * 35.0},
                                                        {40.0, 1.5 * 40.0},
                                                        {45.0, 1.5 * 45.0}});
    Fuel *fuel = engine->getFuel();
    fuel->initialize(fp);

    CombustionChamber::Parameters ccp;
    ccp.CrankcasePressure = units::pressure(1.0, units::atm);
    ccp.Fuel = fuel;
    ccp.StartingPressure = units::pressure(1.0, units::atm);
    ccp.StartingTemperature = units::celcius(25.0);
    ccp.MeanPistonSpeedToTurbulence = meanPistonSpeedToTurbulence;
    for (int i = 0; i < engine->getCylinderCount(); ++i) {
        ccp.Piston = engine->getPiston(i);
        ccp.Head = engine->getHead(ccp.Piston->getCylinderBank()->getIndex());
        engine->getChamber(i)->initialize(ccp);
    }

    engine->calculateDisplacement();

    out.engine = engine;
    out.vehicle = nullptr;       // like the script: the Instance supplies upstream's default vehicle
    out.transmission = nullptr;  // and engine-sim's transmission stays in neutral (UnityTransmission)
    out.name = "v8_bigblock_a";
    out.peakTorqueHint = 0.0;    // set from the measured dyno below in buildBuiltin's table (builders.cpp)
    out.idleRpmHint = 650.0;
    out.sourceKind = 1;
    return true;
}

} // namespace esp
