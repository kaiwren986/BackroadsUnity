using System;
using System.IO;
using Unity.Mathematics;

namespace Backroads.Vehicles
{
    /// <summary>
    /// The byte form of a <see cref="BrvState"/> (ADR-005): little-endian, explicit field order, every value at full
    /// precision (IEEE doubles), so restoring a snapshot continues the simulation exactly as if it had never stopped.
    /// Layout: magic "BRVS", u16 version, u16 wheel count, then the body, controls, controller, winch and per-wheel
    /// blocks in the order written below (version 1, frozen at m2); version 2 (BRV v2, T3.5) appends the shaft block:
    /// the driveline (crank, gearbox output, clutch, lockers, warm starts) and per wheel the v2 solver state. Readers
    /// take versions 1 and 2 (a version 1 snapshot leaves the v2 block empty; the first v2 step re-syncs it), and
    /// reject other magics, unknown versions and wheel counts that differ from the vehicle definition.
    /// </summary>
    public static class BrvSnapshot
    {
        public const uint Magic = 0x53565242; // "BRVS" little-endian
        public const ushort Version = 2;
        public const ushort Version1 = 1;

        public static byte[] ToBytes(in BrvState s, ushort version = Version)
        {
            using var ms = new MemoryStream(2048);
            using (var w = new BinaryWriter(ms)) Write(w, s, version);
            return ms.ToArray();
        }

        public static BrvState FromBytes(byte[] bytes, int expectedWheels = -1)
        {
            using var ms = new MemoryStream(bytes ?? throw new ArgumentNullException(nameof(bytes)));
            using var r = new BinaryReader(ms);
            return Read(r, expectedWheels);
        }

        public static void Write(BinaryWriter w, in BrvState s, ushort version = Version)
        {
            if (version != Version1 && version != Version) throw new ArgumentOutOfRangeException(nameof(version));
            w.Write(Magic);
            w.Write(version);
            w.Write((ushort)s.wheels.Length);
            W3(w, s.pos); W3(w, s.vel); W3(w, s.angVel); W3(w, s.prevPos);
            W4(w, s.quat); W4(w, s.prevQuat);
            W3(w, s.ex); W3(w, s.ey); W3(w, s.ez); W3(w, s.iI);
            w.Write(s.mass); w.Write(s.invMass); w.Write(s.payload);
            w.Write(s.ctrl.throttle); w.Write(s.ctrl.brake); w.Write(s.ctrl.steer); w.Write(s.ctrl.handbrake);
            w.Write(s.steerIn); w.Write(s.steerAngle);
            w.Write(s.gear);
            w.Write(s.rpm); w.Write(s.shiftT); w.Write(s.revHold); w.Write(s.fwdHold); w.Write(s.fuel);
            byte flags = (byte)((s.lightsOn ? 1 : 0) | (s.horn ? 2 : 0) | (s.engineOn ? 4 : 0) | (s.asleep ? 8 : 0) | (s.driven ? 16 : 0) | (s.lowRange ? 32 : 0));
            w.Write(flags);
            w.Write(s.sleepT); w.Write(s.impact); W3(w, s.impactDir);
            w.Write(s.inWater); w.Write(s.speed); w.Write(s.fwdSpeed);
            w.Write(s.deformTick);
            w.Write(s.dirt); w.Write(s.mudSink); w.Write(s.stuckT);
            w.Write(s.omegaMax); w.Write(s.engineBrake);
            w.Write(s.nContact);
            w.Write(s.trackSpeed.x); w.Write(s.trackSpeed.y);
            w.Write(s.winch.active); W3(w, s.winch.anchor); w.Write(s.winch.target); w.Write(s.winch.len); w.Write(s.winch.reel); w.Write(s.winch.tension);
            w.Write(s.steps);
            for (int i = 0; i < s.wheels.Length; i++)
            {
                var h = s.wheels[i];
                w.Write((byte)((h.contact ? 1 : 0) | (h.sliding ? 2 : 0)));
                w.Write(h.surf);
                w.Write(h.len); w.Write(h.comp); w.Write(h.load);
                W3(w, h.cp); W3(w, h.n);
                w.Write(h.omega); w.Write(h.angle); w.Write(h.spin); w.Write(h.skid); w.Write(h.vl); w.Write(h.mudRes);
                W3(w, h.fd); W3(w, h.sd); W3(w, h.arm); W3(w, h.rS);
                w.Write(h.kF); w.Write(h.kS); w.Write(h.accL); w.Write(h.accS); w.Write(h.longLim); w.Write(h.latLim); w.Write(h.vsPre);
            }
            if (version < 2) return;
            // ---- version 2: the shaft block (BRV v2)
            var dl = s.dl;
            w.Write((byte)((dl.init ? 1 : 0) | (dl.lockFront ? 2 : 0) | (dl.lockRear ? 4 : 0) | (dl.absOff ? 8 : 0)));
            w.Write(dl.crankOmega); w.Write(dl.crankAngle); w.Write(dl.driveOmega); w.Write(dl.clutch);
            w.Write(dl.lamClutch); w.Write(dl.lamDiffFront); w.Write(dl.lamDiffRear); w.Write(dl.engineTorque); w.Write(dl.tcCut);
            for (int i = 0; i < s.wheels.Length; i++)
            {
                var h = s.wheels[i];
                w.Write(h.lamB); w.Write(h.lamT); w.Write(h.lamL); w.Write(h.lamM);
                w.Write(h.anchorL); w.Write(h.anchorS); w.Write(h.anchorB); w.Write(h.absCut);
                w.Write(h.kappa); w.Write(h.alpha);
            }
        }

        public static BrvState Read(BinaryReader r, int expectedWheels = -1)
        {
            if (r.ReadUInt32() != Magic) throw new InvalidDataException("not a BRV snapshot");
            ushort v = r.ReadUInt16();
            if (v != Version1 && v != Version) throw new InvalidDataException($"BRV snapshot version {v}, this build reads {Version1} and {Version}");
            int n = r.ReadUInt16();
            if (n > BrvDef.MaxWheels || (expectedWheels >= 0 && n != expectedWheels))
                throw new InvalidDataException($"BRV snapshot has {n} wheels, expected {expectedWheels}");
            var s = new BrvState();
            s.pos = R3(r); s.vel = R3(r); s.angVel = R3(r); s.prevPos = R3(r);
            s.quat = R4(r); s.prevQuat = R4(r);
            s.ex = R3(r); s.ey = R3(r); s.ez = R3(r); s.iI = R3(r);
            s.mass = r.ReadDouble(); s.invMass = r.ReadDouble(); s.payload = r.ReadDouble();
            s.ctrl.throttle = r.ReadDouble(); s.ctrl.brake = r.ReadDouble(); s.ctrl.steer = r.ReadDouble(); s.ctrl.handbrake = r.ReadDouble();
            s.steerIn = r.ReadDouble(); s.steerAngle = r.ReadDouble();
            s.gear = r.ReadInt32();
            s.rpm = r.ReadDouble(); s.shiftT = r.ReadDouble(); s.revHold = r.ReadDouble(); s.fwdHold = r.ReadDouble(); s.fuel = r.ReadDouble();
            byte flags = r.ReadByte();
            s.lightsOn = (flags & 1) != 0; s.horn = (flags & 2) != 0; s.engineOn = (flags & 4) != 0;
            s.asleep = (flags & 8) != 0; s.driven = (flags & 16) != 0; s.lowRange = (flags & 32) != 0;
            s.sleepT = r.ReadDouble(); s.impact = r.ReadDouble(); s.impactDir = R3(r);
            s.inWater = r.ReadDouble(); s.speed = r.ReadDouble(); s.fwdSpeed = r.ReadDouble();
            s.deformTick = r.ReadInt32();
            s.dirt = r.ReadDouble(); s.mudSink = r.ReadDouble(); s.stuckT = r.ReadDouble();
            s.omegaMax = r.ReadDouble(); s.engineBrake = r.ReadDouble();
            s.nContact = r.ReadInt32();
            s.trackSpeed = new double2(r.ReadDouble(), r.ReadDouble());
            s.winch.active = r.ReadBoolean(); s.winch.anchor = R3(r); s.winch.target = r.ReadInt32(); s.winch.len = r.ReadDouble();
            s.winch.reel = r.ReadBoolean(); s.winch.tension = r.ReadDouble();
            s.steps = r.ReadInt64();
            for (int i = 0; i < n; i++)
            {
                var h = new BrvWheel();
                byte f = r.ReadByte();
                h.contact = (f & 1) != 0; h.sliding = (f & 2) != 0;
                h.surf = r.ReadByte();
                h.len = r.ReadDouble(); h.comp = r.ReadDouble(); h.load = r.ReadDouble();
                h.cp = R3(r); h.n = R3(r);
                h.omega = r.ReadDouble(); h.angle = r.ReadDouble(); h.spin = r.ReadDouble(); h.skid = r.ReadDouble(); h.vl = r.ReadDouble(); h.mudRes = r.ReadDouble();
                h.fd = R3(r); h.sd = R3(r); h.arm = R3(r); h.rS = R3(r);
                h.kF = r.ReadDouble(); h.kS = r.ReadDouble(); h.accL = r.ReadDouble(); h.accS = r.ReadDouble();
                h.longLim = r.ReadDouble(); h.latLim = r.ReadDouble(); h.vsPre = r.ReadDouble();
                s.wheels.Add(h);
            }
            if (v < 2) return s;
            byte fl = r.ReadByte();
            s.dl.init = (fl & 1) != 0; s.dl.lockFront = (fl & 2) != 0; s.dl.lockRear = (fl & 4) != 0; s.dl.absOff = (fl & 8) != 0;
            s.dl.crankOmega = r.ReadDouble(); s.dl.crankAngle = r.ReadDouble(); s.dl.driveOmega = r.ReadDouble(); s.dl.clutch = r.ReadDouble();
            s.dl.lamClutch = r.ReadDouble(); s.dl.lamDiffFront = r.ReadDouble(); s.dl.lamDiffRear = r.ReadDouble(); s.dl.engineTorque = r.ReadDouble(); s.dl.tcCut = r.ReadDouble();
            for (int i = 0; i < n; i++)
            {
                ref var h = ref s.wheels.ElementAt(i);
                h.lamB = r.ReadDouble(); h.lamT = r.ReadDouble(); h.lamL = r.ReadDouble(); h.lamM = r.ReadDouble();
                h.anchorL = r.ReadDouble(); h.anchorS = r.ReadDouble(); h.anchorB = r.ReadDouble(); h.absCut = r.ReadDouble();
                h.kappa = r.ReadDouble(); h.alpha = r.ReadDouble();
            }
            return s;
        }

        static void W3(BinaryWriter w, double3 v) { w.Write(v.x); w.Write(v.y); w.Write(v.z); }
        static void W4(BinaryWriter w, double4 v) { w.Write(v.x); w.Write(v.y); w.Write(v.z); w.Write(v.w); }
        static double3 R3(BinaryReader r) => new double3(r.ReadDouble(), r.ReadDouble(), r.ReadDouble());
        static double4 R4(BinaryReader r) => new double4(r.ReadDouble(), r.ReadDouble(), r.ReadDouble(), r.ReadDouble());
    }
}
