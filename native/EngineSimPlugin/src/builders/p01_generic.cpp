// p01_generic.cpp: see p01_generic.h. Ported verbatim from probes/p01/bench.cpp (buildEngine, specInline,
// specLsV8, benchEngine's vehicle and transmission); only the names are fictional ids.
#include "p01_generic.h"

#include "builder_util.h"

#include "direct_throttle_linkage.h"
#include "engine.h"
#include "impulse_response.h"
#include "standard_valvetrain.h"
#include "transmission.h"
#include "vehicle.h"

#include <cmath>

namespace esp {
namespace p01 {

using namespace build;

namespace {

double deg(double d) { return units::angle(d, units::deg); }

Engine *buildEngine(const Spec &sp, const std::string &irPath) {
    const double stroke = units::distance(3.622, units::inch);
    const double bore = units::distance(3.78, units::inch);
    const double rodLen = units::distance(160, units::mm);
    const double rodMass = units::mass(50, units::g);
    const double compH = units::distance(1.0, units::inch);
    const double crankMass = units::mass(60, units::lb);
    const double flyMass = units::mass(30, units::lb);
    const double flyR = units::distance(8, units::inch);
    const int nCyl = static_cast<int>(sp.cyl.size());
    const int nBanks = static_cast<int>(sp.bankAnglesDeg.size());
    const int nExh = static_cast<int>(sp.exhaustLengthIn.size());

    Engine::Parameters ep;
    ep.name = sp.name;
    ep.cylinderBanks = nBanks;
    ep.cylinderCount = nCyl;
    ep.crankshaftCount = 1;
    ep.exhaustSystemCount = nExh;
    ep.intakeCount = 1;
    ep.starterTorque = units::torque(200, units::ft_lb);
    ep.starterSpeed = units::rpm(200);
    ep.redline = units::rpm(sp.redlineRpm);
    ep.dynoMinSpeed = units::rpm(1000);
    ep.dynoMaxSpeed = ep.redline;
    ep.dynoHoldStep = units::rpm(100);
    DirectThrottleLinkage *thr = new DirectThrottleLinkage;
    DirectThrottleLinkage::Parameters tp;
    tp.gamma = 2.0;
    thr->initialize(tp);
    ep.throttle = thr;
    ep.initialSimulationFrequency = sp.simFreq;
    ep.initialHighFrequencyGain = 0.01;
    ep.initialNoise = 1.0;
    ep.initialJitter = 0.6;

    Engine *e = new Engine;
    e->initialize(ep);

    Crankshaft *crank = e->getCrankshaft(0);
    Crankshaft::Parameters cp;
    cp.mass = crankMass;
    cp.flywheelMass = flyMass;
    cp.momentOfInertia = 1.5 * 0.5 * crankMass * stroke * stroke + 0.5 * flyMass * flyR * flyR + 0.5 * 1.0 * 0.01 * 0.01;
    cp.crankThrow = stroke / 2;
    cp.pos_x = 0;
    cp.pos_y = 0;
    cp.tdc = deg(sp.tdcDeg);
    cp.frictionTorque = units::torque(20.0 * nCyl / 8.0, units::ft_lb);
    cp.rodJournals = static_cast<int>(sp.journalAnglesDeg.size());
    crank->initialize(cp);
    for (int i = 0; i < cp.rodJournals; ++i) crank->setRodJournalAngle(i, deg(sp.journalAnglesDeg[i]));

    Intake *intake = e->getIntake(0);
    Intake::Parameters ip;
    ip.volume = units::volume(1.325 * nCyl / 8.0, units::L);
    ip.CrossSectionArea = units::area(20.0, units::cm2);
    ip.InputFlowK = GasSystem::k_carb(700.0 * nCyl / 8.0);
    ip.IdleFlowK = GasSystem::k_carb(0.0);
    ip.RunnerFlowRate = GasSystem::k_carb(100.0);
    ip.RunnerLength = units::distance(12.0, units::inch);
    ip.IdleThrottlePlatePosition = 0.996;
    ip.VelocityDecay = 0.5;
    intake->initialize(ip);

    for (int x = 0; x < nExh; ++x) {
        ImpulseResponse *ir = new ImpulseResponse;
        ir->initialize(irPath, 0.001);
        ExhaustSystem::Parameters xp;
        xp.length = units::distance(sp.exhaustLengthIn[x], units::inch);
        xp.collectorCrossSectionArea =
            constants::pi * units::distance(2.0, units::inch) * units::distance(2.0, units::inch);
        xp.outletFlowRate = GasSystem::k_carb(1000.0);
        xp.primaryTubeLength = units::distance(29.0, units::inch);
        xp.primaryFlowRate = GasSystem::k_carb(500.0);
        xp.velocityDecay = 1.0;
        xp.audioVolume = 4.0;
        xp.impulseResponse = ir;
        e->getExhaustSystem(x)->initialize(xp);
    }

    const Samples inFlow = {{0, 0},     {50, 1},    {100, 103}, {150, 156}, {200, 214},
                            {250, 249}, {300, 268}, {350, 280}, {400, 280}, {450, 281}};
    const Samples exFlow = {{0, 0},     {50, 1},    {100, 72},  {150, 113}, {200, 160},
                            {250, 196}, {300, 222}, {350, 235}, {400, 245}, {450, 246}};
    Function *inLobe = harmonicCamLobe(deg(234), 1.1, units::distance(551, units::thou), 256);
    Function *exLobe = harmonicCamLobe(deg(235), 1.1, units::distance(551, units::thou), 256);

    int g = 0;
    for (int b = 0; b < nBanks; ++b) {
        std::vector<CylSpec> bc;
        for (const CylSpec &c : sp.cyl)
            if (c.bank == b) bc.push_back(c);
        CylinderBank *bank = e->getCylinderBank(b);
        CylinderBank::Parameters bp;
        bp.crankshaft = crank;
        bp.positionX = 0;
        bp.positionY = 0;
        bp.angle = deg(sp.bankAnglesDeg[b]);
        bp.bore = bore;
        bp.deckHeight = stroke / 2 + rodLen + compH;
        bp.displayDepth = 0;
        bp.cylinderCount = static_cast<int>(bc.size());
        bp.index = b;
        bank->initialize(bp);

        for (int i = 0; i < static_cast<int>(bc.size()); ++i) {
            Piston *piston = e->getPiston(g + i);
            ConnectingRod *rod = e->getConnectingRod(g + i);
            Piston::Parameters pp;
            pp.Rod = rod;
            pp.Bank = bank;
            pp.CylinderIndex = i;
            pp.BlowbyFlowCoefficient = GasSystem::k_28inH2O(0.0);
            pp.CompressionHeight = compH;
            pp.WristPinPosition = 0;
            pp.Displacement = 0;
            pp.mass = units::mass(100, units::g);
            piston->initialize(pp);
            ConnectingRod::Parameters rp;
            rp.mass = rodMass;
            rp.momentOfInertia = (1 / 12.0) * rodMass * rodLen * rodLen;
            rp.centerOfMass = 0;
            rp.length = rodLen;
            rp.rodJournals = 0;
            rp.slaveThrow = 0;
            rp.piston = piston;
            rp.crankshaft = crank;
            rp.master = nullptr;
            rp.journal = bc[i].journal;
            rod->initialize(rp);
        }

        Camshaft *inCam = new Camshaft, *exCam = new Camshaft;
        Camshaft::Parameters cam;
        cam.lobes = static_cast<int>(bc.size());
        cam.advance = 0;
        cam.crankshaft = crank;
        cam.baseRadius = units::distance(1.0, units::inch);
        cam.lobeProfile = inLobe;
        inCam->initialize(cam);
        cam.lobeProfile = exLobe;
        exCam->initialize(cam);
        for (int i = 0; i < static_cast<int>(bc.size()); ++i) {
            inCam->setLobeCenterline(i, deg(bc[i].firingDeg + 360 + 116));
            exCam->setLobeCenterline(i, deg(bc[i].firingDeg + 360 - 116));
        }
        StandardValvetrain *vt = new StandardValvetrain;
        StandardValvetrain::Parameters vp;
        vp.intakeCamshaft = inCam;
        vp.exhaustCamshaft = exCam;
        vt->initialize(vp);

        CylinderHead *head = e->getHead(b);
        CylinderHead::Parameters hp;
        hp.Bank = bank;
        hp.IntakePortFlow = makeFlowFunction(inFlow, units::distance(50, units::thou));
        hp.ExhaustPortFlow = makeFlowFunction(exFlow, units::distance(50, units::thou));
        hp.Valvetrain = vt;
        hp.CombustionChamberVolume = units::volume(90, units::cc);
        hp.IntakeRunnerVolume = units::volume(149.6, units::cc);
        hp.IntakeRunnerCrossSectionArea = units::distance(2.2, units::inch) * units::distance(2.2, units::inch);
        hp.ExhaustRunnerVolume = units::volume(50.0, units::cc);
        hp.ExhaustRunnerCrossSectionArea = units::distance(1.75, units::inch) * units::distance(1.75, units::inch);
        hp.FlipDisplay = false;
        head->initialize(hp);
        for (int i = 0; i < static_cast<int>(bc.size()); ++i) {
            head->setIntake(i, intake);
            head->setExhaustSystem(i, e->getExhaustSystem(bc[i].exhaust));
            head->setSoundAttenuation(i, 1.0);
            head->setHeaderPrimaryLength(i, bc[i].primaryLen);
        }
        g += static_cast<int>(bc.size());
    }

    Function *timing = makeFunction(units::rpm(1000), {{units::rpm(0), deg(12)},
                                                       {units::rpm(1000), deg(12)},
                                                       {units::rpm(2000), deg(20)},
                                                       {units::rpm(3000), deg(30)},
                                                       {units::rpm(4000), deg(40)},
                                                       {units::rpm(5000), deg(40)},
                                                       {units::rpm(6000), deg(40)},
                                                       {units::rpm(7000), deg(40)},
                                                       {units::rpm(8000), deg(40)}});
    IgnitionModule::Parameters ig;
    ig.cylinderCount = nCyl;
    ig.crankshaft = crank;
    ig.timingCurve = timing;
    ig.revLimit = units::rpm(sp.revLimitRpm);
    ig.limiterDuration = 0.2;
    e->getIgnitionModule()->initialize(ig);
    {
        int gi = 0;
        for (int b = 0; b < nBanks; ++b)
            for (const CylSpec &c : sp.cyl)
                if (c.bank == b) e->getIgnitionModule()->setFiringOrder(gi++, deg(c.firingDeg));
    }

    Samples tfs = {{0.0, 3.0}};
    for (int i = 1; i <= 9; ++i) {
        const double x = 5.0 * i;
        tfs.push_back({x, (i == 1 ? 1.5 : (i == 2 ? 1.75 : 2.0)) * x});
    }
    Fuel::Parameters fp;
    fp.maxBurningEfficiency = 1.0;
    fp.maxDilutionEffect = 10.0;  // .mr default (the C++ struct default is 50)
    fp.turbulenceToFlameSpeedRatio = makeFunction(5.0, tfs);
    e->getFuel()->initialize(fp);

    Samples mps;
    for (int i = 0; i < 30; ++i) mps.push_back({static_cast<double>(i), i * 0.5});
    Function *meanPistonSpeedToTurbulence = makeFunction(1.0, mps);

    CombustionChamber::Parameters cc;
    cc.CrankcasePressure = units::pressure(1.0, units::atm);
    cc.Fuel = e->getFuel();
    cc.StartingPressure = units::pressure(1.0, units::atm);
    cc.StartingTemperature = units::celcius(25.0);
    cc.MeanPistonSpeedToTurbulence = meanPistonSpeedToTurbulence;
    for (int i = 0; i < nCyl; ++i) {
        cc.Piston = e->getPiston(i);
        cc.Head = e->getHead(cc.Piston->getCylinderBank()->getIndex());
        e->getChamber(i)->initialize(cc);
    }
    e->calculateDisplacement();
    return e;
}

} // namespace

Spec specInline(int n, double simFreq) {
    Spec s;
    s.name = "i" + std::to_string(n) + "_synth";
    s.bankAnglesDeg = {0.0};
    s.tdcDeg = 90.0;
    for (int i = 0; i < n; ++i) {
        const double phi = i * 720.0 / n;
        s.journalAnglesDeg.push_back(std::fmod(phi, 360.0));
        s.cyl.push_back({0, i, phi, units::distance(2.0 * i, units::inch), 0});
    }
    s.exhaustLengthIn = {100.0};
    s.simFreq = simFreq;
    s.redlineRpm = 6500;
    s.revLimitRpm = 6800;
    return s;
}

Spec specCrossPlaneV8(double simFreq) {
    Spec s;
    s.name = "v8_cross_a";
    s.bankAnglesDeg = {-45.0, 45.0};
    s.journalAnglesDeg = {0, 270, 90, 180};
    s.tdcDeg = 45.0;
    const double sp = units::distance(2, units::inch), cm = units::distance(1, units::cm);
    s.cyl = {{0, 0, 0, 3 * sp + 2 * cm, 0},   {0, 1, 630, 2 * sp + 1 * cm, 0}, {0, 2, 450, 1 * sp + 3 * cm, 0},
             {0, 3, 180, 0 * sp + 5 * cm, 0}, {1, 0, 270, 3 * sp + 1 * cm, 1}, {1, 1, 540, 2 * sp + 5 * cm, 1},
             {1, 2, 360, 1 * sp + 7 * cm, 1}, {1, 3, 90, 0, 1}};
    s.exhaustLengthIn = {100.0, 172.0};
    s.simFreq = simFreq;
    s.redlineRpm = 6500;
    s.revLimitRpm = 6800;
    return s;
}

bool build(const Spec &spec, const std::string &esDir, EngineBundle &out) {
    Engine *e = buildEngine(spec, esDir + "/sound-library/smooth/smooth_39.wav");

    Vehicle *veh = new Vehicle;
    Vehicle::Parameters vp;
    vp.mass = units::mass(1614, units::kg);
    vp.dragCoefficient = 0.3;
    vp.crossSectionArea = units::distance(72, units::inch) * units::distance(50, units::inch);
    vp.diffRatio = 3.42;
    vp.tireRadius = units::distance(10, units::inch);
    vp.rollingResistance = 200;
    veh->initialize(vp);

    static const double ratios[] = {2.97, 2.07, 1.43, 1.00, 0.71, 0.57};
    Transmission *tr = new Transmission;
    Transmission::Parameters tp;
    tp.GearCount = 6;
    tp.GearRatios = ratios;
    tp.MaxClutchTorque = units::torque(500, units::ft_lb);
    tr->initialize(tp);

    out.engine = e;
    out.vehicle = veh;
    out.transmission = tr;
    out.name = spec.name;
    out.sourceKind = 1;
    return true;
}

} // namespace p01
} // namespace esp
