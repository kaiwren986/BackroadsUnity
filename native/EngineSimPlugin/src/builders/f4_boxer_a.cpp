// f4_boxer_a: a 2.457 L flat-four, the line-by-line C++ port of upstream's default example engine
// (vendor/engine-sim/assets/engines/atg-video-2/01_*_eh.mr, loaded by upstream's assets/main.mr) made by research 02
// (probes/p02/src/ej25.cpp). Values are copied from that script and the defaults in es/objects/objects.mr,
// es/actions/actions.mr, es/utilities/utilities.mr and es/sound-library/impulse_responses.mr; the order of initialize()
// calls mirrors es_script::EngineNode::buildEngine(). Validated in 02 6.2: 229 N m at 4,500 rpm, 170 hp at 6,500 rpm.
#include "builders.h"
#include "builder_util.h"

#include "direct_throttle_linkage.h"
#include "engine.h"
#include "impulse_response.h"
#include "standard_valvetrain.h"
#include "transmission.h"
#include "vehicle.h"

namespace esp {

using namespace build;

namespace {

struct CylinderSpec {
    int rodJournal;
    double blowbyScfm;
    double primaryLength;
    double soundAttenuation;
};

} // namespace

bool buildF4BoxerA(const std::string &esDir, EngineBundle &out) {
    Engine *engine = new Engine;

    DirectThrottleLinkage *throttle = new DirectThrottleLinkage;
    DirectThrottleLinkage::Parameters throttleParams;
    throttleParams.gamma = 2.0;
    throttle->initialize(throttleParams);

    Engine::Parameters ep;
    ep.name = "f4_boxer_a";
    ep.starterTorque = units::torque(70, units::ft_lb);
    ep.starterSpeed = units::rpm(500);
    ep.redline = units::rpm(6500);
    ep.dynoMinSpeed = units::rpm(1000);
    ep.dynoMaxSpeed = ep.redline;
    ep.dynoHoldStep = units::rpm(100);
    ep.throttle = throttle;
    ep.initialSimulationFrequency = 20000;
    ep.initialHighFrequencyGain = 0.01;
    ep.initialNoise = 1.0;
    ep.initialJitter = 0.5;
    ep.crankshaftCount = 1;
    ep.cylinderBanks = 2;
    ep.cylinderCount = 4;
    ep.exhaustSystemCount = 1;
    ep.intakeCount = 1;
    engine->initialize(ep);

    const double stroke = 79 * units::mm;
    const double bore = 99.5 * units::mm;
    const double rodLength = 5.142 * units::inch;
    const double rodMass = 535 * units::g;
    const double compressionHeight = 1.0 * units::inch;
    const double crankMass = 9.39 * units::kg;
    const double flywheelMass = 6.8 * units::kg;
    const double flywheelRadius = 6 * units::inch;

    Crankshaft *c0 = engine->getCrankshaft(0);
    Crankshaft::Parameters cp;
    cp.crankThrow = stroke / 2;
    cp.flywheelMass = flywheelMass;
    cp.mass = crankMass;
    cp.frictionTorque = 1.0 * units::ft_lb;
    cp.momentOfInertia = diskMoment(crankMass, stroke / 2) + diskMoment(flywheelMass, flywheelRadius) * 2 +
                         diskMoment(10 * units::kg, 6.0 * units::cm);
    cp.pos_x = 0.0;
    cp.pos_y = 0.0;
    cp.tdc = 180 * units::deg;
    cp.rodJournals = 4;
    c0->initialize(cp);
    c0->setRodJournalAngle(0, 0.0 * units::deg);
    c0->setRodJournalAngle(1, 180.0 * units::deg);
    c0->setRodJournalAngle(2, 0.0 * units::deg);
    c0->setRodJournalAngle(3, 180.0 * units::deg);

    Function *intakeLobe = harmonicCamLobe(232 * units::deg, 2.0, 9.78 * units::mm, 100);
    Function *exhaustLobe = harmonicCamLobe(236 * units::deg, 2.0, 9.60 * units::mm, 100);

    const Samples intakeFlowCfm = {{0, 0},     {50, 58},   {100, 103}, {150, 156}, {200, 214},
                                   {250, 249}, {300, 268}, {350, 280}, {400, 280}, {450, 281}};
    const Samples exhaustFlowCfm = {{0, 0},     {50, 37},   {100, 72},  {150, 113}, {200, 160},
                                    {250, 196}, {300, 222}, {350, 235}, {400, 245}, {450, 246}};

    Intake *intake = engine->getIntake(0);
    Intake::Parameters ip;
    ip.volume = 1.325 * units::L;
    ip.CrossSectionArea = 20.0 * units::cm2;
    ip.InputFlowK = GasSystem::k_carb(400.0);
    ip.RunnerFlowRate = GasSystem::k_carb(100.0);
    ip.RunnerLength = 12.0 * units::inch;
    ip.IdleFlowK = GasSystem::k_carb(0.0);
    ip.IdleThrottlePlatePosition = 0.9978;
    ip.VelocityDecay = 1.0;
    ip.MolecularAfr = 25.0 / 2.0;

    ImpulseResponse *ir = new ImpulseResponse;
    ir->initialize(esDir + "/sound-library/new/minimal_muffling_02.wav", 0.01);

    ExhaustSystem *exhaust0 = engine->getExhaustSystem(0);
    ExhaustSystem::Parameters xp;
    xp.length = 500 * units::mm;
    xp.collectorCrossSectionArea = constants::pi * (2.0 * units::inch) * (2.0 * units::inch);
    xp.outletFlowRate = GasSystem::k_carb(1000.0);
    xp.primaryTubeLength = 40.0 * units::inch;
    xp.primaryFlowRate = GasSystem::k_carb(400.0);
    xp.velocityDecay = 1.0;
    xp.audioVolume = 0.5 * 0.02;
    xp.impulseResponse = ir;

    const CylinderSpec cylinders[4] = {
        {0, 0.001, 2.0 * units::inch, 0.9},  // bank 0, wire 1
        {3, 0.002, 3.0 * units::inch, 1.0},  // bank 0, wire 3
        {1, 0.001, 3.0 * units::inch, 1.1},  // bank 1, wire 2
        {2, 0.002, 5.0 * units::inch, 0.9},  // bank 1, wire 4
    };

    int global = 0;
    for (int b = 0; b < 2; ++b) {
        CylinderBank *bank = engine->getCylinderBank(b);
        CylinderBank::Parameters bp;
        bp.bore = bore;
        bp.deckHeight = stroke / 2 + rodLength + compressionHeight;
        bp.positionX = 0.0;
        bp.positionY = 0.0;
        bp.displayDepth = 0.5;
        bp.angle = (b == 0 ? 90.0 : -90.0) * units::deg;
        bp.cylinderCount = 2;
        bp.index = b;
        bp.crankshaft = c0;
        bank->initialize(bp);

        const int firstGlobal = global;
        for (int i = 0; i < 2; ++i, ++global) {
            const CylinderSpec &spec = cylinders[global];
            Piston *piston = engine->getPiston(global);
            ConnectingRod *rod = engine->getConnectingRod(global);

            Piston::Parameters pp;
            pp.mass = (414 + 152) * units::g;
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
            rp.momentOfInertia = rodMoment(rodMass, rodLength);
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

        Camshaft *intakeCam = new Camshaft;
        Camshaft *exhaustCam = new Camshaft;
        Camshaft::Parameters camp;
        camp.advance = 0.0;
        camp.baseRadius = (34.0 / 2) * units::mm;
        camp.crankshaft = c0;
        camp.lobes = 2;
        camp.lobeProfile = intakeLobe;
        intakeCam->initialize(camp);
        camp.lobeProfile = exhaustLobe;
        exhaustCam->initialize(camp);

        const double rot360 = 360 * units::deg;
        const double quarterCycle = 180 * units::deg;
        for (int lobe = 0; lobe < 2; ++lobe) {
            const double offset = (2 * b + lobe) * quarterCycle;
            exhaustCam->setLobeCenterline(lobe, rot360 - 112 * units::deg + offset);
            intakeCam->setLobeCenterline(lobe, rot360 + 117 * units::deg + offset);
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
        hp.CombustionChamberVolume = 67 * units::cc;
        hp.IntakeRunnerVolume = 149.6 * units::cc;
        hp.IntakeRunnerCrossSectionArea = (1.75 * units::inch) * (1.75 * units::inch);
        hp.ExhaustRunnerVolume = 50.0 * units::cc;
        hp.ExhaustRunnerCrossSectionArea = (1.25 * units::inch) * (1.25 * units::inch);
        hp.FlipDisplay = (b == 0);
        head->initialize(hp);

        for (int i = 0; i < 2; ++i) {
            const CylinderSpec &spec = cylinders[firstGlobal + i];
            exhaust0->initialize(xp);  // the script node re-initialises the shared object per cylinder too
            intake->initialize(ip);
            head->setIntake(i, intake);
            head->setExhaustSystem(i, exhaust0);
            head->setSoundAttenuation(i, spec.soundAttenuation);
            head->setHeaderPrimaryLength(i, spec.primaryLength);
        }
    }

    Function *timing = makeFunction(units::rpm(1000), {{units::rpm(0), 25 * units::deg},
                                                       {units::rpm(1000), 25 * units::deg},
                                                       {units::rpm(2000), 30 * units::deg},
                                                       {units::rpm(3000), 40 * units::deg},
                                                       {units::rpm(4000), 40 * units::deg}});

    IgnitionModule::Parameters ignp;
    ignp.crankshaft = c0;
    ignp.cylinderCount = 4;
    ignp.revLimit = units::rpm(6800);
    ignp.timingCurve = timing;
    ignp.limiterDuration = 0.16;
    engine->getIgnitionModule()->initialize(ignp);
    engine->getIgnitionModule()->setFiringOrder(0, 0.0 * units::deg);
    engine->getIgnitionModule()->setFiringOrder(1, 180.0 * units::deg);
    engine->getIgnitionModule()->setFiringOrder(2, 360.0 * units::deg);
    engine->getIgnitionModule()->setFiringOrder(3, 540.0 * units::deg);

    Function *meanPistonSpeedToTurbulence = new Function;
    meanPistonSpeedToTurbulence->initialize(30, 1);
    for (int i = 0; i < 30; ++i) {
        const double s = static_cast<double>(i);
        meanPistonSpeedToTurbulence->addSample(s, s * 0.5);
    }

    Fuel::Parameters fp;  // objects.mr fuel{} defaults (max_dilution_effect is 10.0 in the script, 50.0 in C++)
    fp.name = "gasoline";
    fp.molecularMass = 100 * units::g;
    fp.energyDensity = 48.1 * units::kJ / units::g;
    fp.density = 0.755 * units::kg / units::L;
    fp.molecularAfr = 25 / 2.0;
    fp.maxBurningEfficiency = 0.9;
    fp.burningEfficiencyRandomness = 0.5;
    fp.lowEfficiencyAttenuation = 0.6;
    fp.maxTurbulenceEffect = 2.0;
    fp.maxDilutionEffect = 10.0;
    fp.turbulenceToFlameSpeedRatio = makeFunction(5.0, {{0.0, 1.0 * 3.0},
                                                        {5.0, 1.0 * 1.5 * 5.0},
                                                        {10.0, 1.0 * 1.5 * 10.0},
                                                        {15.0, 1.1 * 1.5 * 15.0},
                                                        {20.0, 1.25 * 1.5 * 20.0},
                                                        {25.0, 1.25 * 1.5 * 25.0},
                                                        {30.0, 1.25 * 1.5 * 30.0},
                                                        {35.0, 1.25 * 1.5 * 35.0},
                                                        {40.0, 1.25 * 1.5 * 40.0},
                                                        {45.0, 1.25 * 1.5 * 45.0}});
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

    const double carMass = 2700 * units::lb;
    Vehicle *vehicle = new Vehicle;
    Vehicle::Parameters vehp;
    vehp.mass = carMass;
    vehp.dragCoefficient = 0.3;
    vehp.crossSectionArea = (72 * units::inch) * (56 * units::inch);
    vehp.diffRatio = 3.9;
    vehp.tireRadius = 10 * units::inch;
    vehp.rollingResistance = 0.015 * carMass * 9.81;
    vehicle->initialize(vehp);

    static const double gears[] = {3.636, 2.375, 1.761, 1.346, 0.971, 0.756};
    Transmission *transmission = new Transmission;
    Transmission::Parameters tp;
    tp.GearCount = 6;
    tp.GearRatios = gears;
    tp.MaxClutchTorque = 300 * units::ft_lb;
    transmission->initialize(tp);

    engine->calculateDisplacement();

    out.engine = engine;
    out.vehicle = vehicle;
    out.transmission = transmission;
    out.name = "f4_boxer_a";
    out.peakTorqueHint = 230.0;
    out.idleRpmHint = 800.0;
    out.sourceKind = 1;
    return true;
}

} // namespace esp
