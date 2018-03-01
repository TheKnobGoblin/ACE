using System;
using System.Collections.Generic;
using System.Numerics;
using ACE.Server.Physics.Collision;
using ACE.Server.Physics.Common;

namespace ACE.Server.Physics.Animation
{
    public enum TransitionState
    {
        Invalid   = 0x0,
        OK        = 0x1,
        Collided  = 0x2,
        Adjusted  = 0x3,
        Slid      = 0x4
    };
    public class Transition
    {
        public ObjectInfo ObjectInfo;
        public SpherePath SpherePath;
        public CollisionInfo CollisionInfo;
        public CellArray CellArray;
        public ObjCell NewCellPtr;

        public static int TransitionLevel;
        public static List<Transition> Transitions;

        public static readonly int MaxTransitionLevel = 10;

        static Transition()
        {
            Transitions = new List<Transition>();
        }

        public Transition()
        {
            Init();
        }

        public Vector3 AdjustOffset(Vector3 offset)
        {
            var collisionAngle = Vector3.Dot(offset, CollisionInfo.SlidingNormal);

            if (CollisionInfo.SlidingNormalValid && collisionAngle >= 0.0f)
                CollisionInfo.SlidingNormalValid = false;

            if (!CollisionInfo.ContactPlaneValid)
                return offset - CollisionInfo.SlidingNormal * collisionAngle;

            var slideAngle = Vector3.Dot(CollisionInfo.ContactPlane.Normal, CollisionInfo.SlidingNormal);
            if (CollisionInfo.SlidingNormalValid)
            {
                var contactSlide = Vector3.Cross(CollisionInfo.ContactPlane.Normal, CollisionInfo.SlidingNormal);
                if (!CollisionInfo.NormalizeCheckSmall(ref contactSlide))
                    offset = Vector3.Dot(contactSlide, offset) * contactSlide;
                else
                    offset = Vector3.Zero;
            }
            else if (slideAngle <= 0.0f)
                offset -= CollisionInfo.ContactPlane.Normal * slideAngle;
            else
                CollisionInfo.ContactPlane.SnapToPlane(offset);

            if (CollisionInfo.ContactPlaneCellID == 0 || CollisionInfo.ContactPlaneIsWater)
                return offset;

            var globSphere = SpherePath.GlobalSphere[0];
            var blockOffset = LandDefs.GetBlockOffset(SpherePath.CheckPos.ObjCellID, CollisionInfo.ContactPlaneCellID);
            var dist = Vector3.Dot(globSphere.Center - blockOffset, CollisionInfo.ContactPlane.Normal);
            if (dist >= globSphere.Radius - PhysicsGlobals.EPSILON)
                return offset;

            var zDist = (globSphere.Radius - dist) / CollisionInfo.ContactPlane.Normal.Z;
            if (globSphere.Radius > Math.Abs(zDist))
            {
                offset = new Vector3(0.0f, 0.0f, zDist);
                SpherePath.AddOffsetToCheckPos(offset);
            }
            return offset;
        }

        public List<ObjCell> BuildCellArray()
        {
            SpherePath.CellArrayValid = true;
            SpherePath.HitsInteriorCell = false;

            return CellArray.find_cell_list(SpherePath);
        }

        public void CalcNumSteps(ref Vector3 offset, ref Vector3 offsetPerStep, ref int numSteps)
        {
            if (SpherePath.BeginPos == null)
            {
                offset = Vector3.Zero;
                offsetPerStep = Vector3.Zero;
                numSteps = 1;
                return;
            }
            offset = SpherePath.BeginPos.GetOffset(SpherePath.EndPos);
            var dist = offset.Length();
            var step = dist / SpherePath.LocalSphere[0].Radius;

            if (!ObjectInfo.State.HasFlag(ObjectInfoState.IsViewer))
            {
                if (step > 1.0f)
                {
                    numSteps = (int)Math.Ceiling(step);
                    offsetPerStep = offset * (1.0f / numSteps);
                }
                else if (!offset.Equals(Vector3.Zero))
                {
                    offsetPerStep = offset;
                    numSteps = 1;
                }
                else
                {
                    offsetPerStep = Vector3.Zero;
                    numSteps = 0;
                }
                return;
            }

            if (dist < PhysicsGlobals.EPSILON)
            {
                offsetPerStep = Vector3.Zero;
                numSteps = 0;
            }
            else
            {
                offsetPerStep = offset * (1.0f / step);
                numSteps = (int)Math.Floor(step) + 1;
            }
        }

        public bool CheckCollisions(PhysicsObj obj)
        {
            SpherePath.InsertType = InsertType.Placement;
            SpherePath.CheckPos.ObjCellID = SpherePath.CurPos.ObjCellID;
            SpherePath.CheckPos.Frame = SpherePath.CurPos.Frame;
            SpherePath.CheckCell = SpherePath.CurCell;
            SpherePath.CellArrayValid = false;
            SpherePath.CacheGlobalSphere(Vector3.Zero);

            return obj.FindObjCollisions(this) != TransitionState.OK;
        }

        public TransitionState CheckOtherCells(ObjCell currCell)
        {
            var result = TransitionState.OK;

            SpherePath.CellArrayValid = true;
            SpherePath.HitsInteriorCell = false;
            var newCell = CellArray.find_cell_list(SpherePath)[0];

            foreach (var cell in CellArray.Cells)
            {
                if (cell == null || cell.Equals(currCell)) continue;
                var collides = cell.FindCollisions(this);
                switch (collides)
                {
                    case TransitionState.Slid:
                        CollisionInfo.ContactPlaneValid = false;
                        CollisionInfo.ContactPlaneIsWater = false;
                        return collides;

                    case TransitionState.Collided:
                    case TransitionState.Adjusted:
                        return collides;
                }
            }

            SpherePath.CheckCell = newCell;
            if (newCell != null)
            {
                SpherePath.AdjustCheckPos(newCell.ID);
                return result;
            }
            if (SpherePath.StepDown) return TransitionState.Collided;

            var checkPos = SpherePath.CheckPos;
            if (checkPos.ObjCellID < 0x100)
                LandDefs.AdjustToOutside(checkPos);

            if (checkPos.ObjCellID != 0)
            {
                SpherePath.AdjustCheckPos(checkPos.ObjCellID);
                SpherePath.SetCheckPos(checkPos, null);

                SpherePath.CellArrayValid = true;

                return result;
            }
            return TransitionState.Collided;
        }

        public bool CheckWalkable(float zCheck)
        {
            if (!ObjectInfo.State.HasFlag(ObjectInfoState.OnWalkable) || SpherePath.CheckWalkables())
                return true;

            SpherePath.BackupCheckPos = SpherePath.CheckPos;
            SpherePath.BackupCell = SpherePath.CheckCell;

            var stepHeight = ObjectInfo.StepDownHeight;

            SpherePath.WalkableAllowance = zCheck;
            SpherePath.CheckWalkable = true;

            var globSphere = SpherePath.GlobalSphere[0];
            if (SpherePath.NumSphere < 2 && stepHeight > globSphere.Radius * 2)
                stepHeight = globSphere.Radius * 0.5f;

            if (stepHeight > globSphere.Radius * 2)
                stepHeight *= 0.5f;

            var offset = new Vector3(0, 0, -stepHeight);
            SpherePath.AddOffsetToCheckPos(offset);

            var transitionState = TransitionalInsert(1);

            SpherePath.CheckWalkable = false;
            SpherePath.SetCheckPos(SpherePath.BackupCheckPos, SpherePath.BackupCell);

            return transitionState != TransitionState.OK;
        }

        public void CleanupTransition()
        {
            --TransitionLevel;
        }

        public TransitionState CliffSlide(Plane contactPlane)
        {
            var contactNormal = Vector3.Cross(contactPlane.Normal, CollisionInfo.LastKnownContactPlane.Normal);
            contactNormal.Z = 0.0f;

            var collideNormal = new Vector3(contactNormal.Z - contactNormal.Y, contactNormal.X - contactNormal.Z, 0.0f);
            if (CollisionInfo.NormalizeCheckSmall(ref collideNormal))
                return TransitionState.OK;

            var blockOffset = LandDefs.GetBlockOffset(SpherePath.CurPos.ObjCellID, SpherePath.CheckPos.ObjCellID);
            var offset = SpherePath.GlobalSphere[0].Center - SpherePath.GlobalCurrCenter[0].Center;

            var angle = Vector3.Dot(collideNormal, offset + blockOffset);
            if (angle <= 0.0f)
            {
                SpherePath.AddOffsetToCheckPos(collideNormal * angle);
                CollisionInfo.SetCollisionNormal(collideNormal);
            }
            else
            {
                SpherePath.AddOffsetToCheckPos(collideNormal * -angle);
                CollisionInfo.SetCollisionNormal(collideNormal * angle);
            }
            return TransitionState.Adjusted;
        }

        public bool EdgeSlide(ref TransitionState transitionState, float stepDownHeight, float zval)
        {
            if (!ObjectInfo.State.HasFlag(ObjectInfoState.OnWalkable) || !ObjectInfo.State.HasFlag(ObjectInfoState.EdgeSlide))
            {
                transitionState = SetEdgeSlide(true, true, TransitionState.OK);
                return true;
            }
            else if (CollisionInfo.ContactPlaneValid && CollisionInfo.ContactPlane.Normal.Z < zval)
            {
                transitionState = SetEdgeSlide(true, false, CliffSlide(CollisionInfo.ContactPlane));
                return false;
            }
            else if (SpherePath.Walkable != null)
            {
                transitionState = SetEdgeSlide(false, false, SpherePath.PrecipiceSlide(this));

                return transitionState == TransitionState.Collided;
            }
            else if (CollisionInfo.ContactPlaneValid)
            {
                transitionState = SetEdgeSlide(true, true, TransitionState.OK);
                return true;
            }
            else
            {
                var offset = SpherePath.GlobalCurrCenter[0].Center - SpherePath.GlobalSphere[0].Center;
                SpherePath.AddOffsetToCheckPos(offset);

                StepDown(stepDownHeight, zval);

                CollisionInfo.ContactPlaneValid = false;
                CollisionInfo.ContactPlaneIsWater = false;
                SpherePath.RestoreCheckPos();

                if (SpherePath.Walkable != null)
                {
                    SpherePath.CacheLocalSpaceSphere(SpherePath.GetWalkablePos(), SpherePath.WalkableScale);
                    SpherePath.SetWalkableCheckPos(SpherePath.LocalSpaceSphere[0]);

                    transitionState = SpherePath.PrecipiceSlide(this);
                    return transitionState == TransitionState.Collided;
                }
                else
                {
                    SpherePath.Walkable = null;
                    SpherePath.CellArrayValid = true;

                    transitionState = TransitionState.Collided;
                    return true;
                }
            }
        }

        public TransitionState SetEdgeSlide(bool unwalkable, bool validCell, TransitionState transitionState)
        {
            if (validCell)  SpherePath.CellArrayValid = true;
            if (unwalkable) SpherePath.Walkable = null;

            CollisionInfo.ContactPlaneValid = false;
            CollisionInfo.ContactPlaneIsWater = false;

            SpherePath.RestoreCheckPos();

            return transitionState;
        }

        public bool FindPlacementPos()
        {
            // refactor me
            SpherePath.SetCheckPos(SpherePath.CurPos, SpherePath.CurCell);

            CollisionInfo.SlidingNormalValid = false;
            CollisionInfo.ContactPlaneValid = false;
            CollisionInfo.ContactPlaneIsWater = false;

            var transitionState = TransitionalInsert(3);

            var redo = 0;
            transitionState = ValidatePlacementTransition(transitionState, ref redo);

            if (transitionState == TransitionState.OK)
                return true;

            if (!SpherePath.PlacementAllowsSliding)
                return false;

            var adjustDist = 4.0f;
            var adjustRad = adjustDist;
            var sphereRad = SpherePath.LocalSphere[0].Radius;

            var fakeSphere = false;

            if (sphereRad < 0.125f)
            {
                fakeSphere = true;
                adjustRad = 2.0f;
            }
            else if (sphereRad < 0.48f)
            {
                sphereRad = 0.48f;
            }

            var delta = sphereRad;

            var step = 4.0f / delta;

            if (fakeSphere)
                step *= 0.5f;

            if (step <= 1.0f)
                return false;

            var numSteps = (int)Math.Ceiling(step);
            var distPerStep = adjustRad / numSteps;
            var radiansPerStep = (float)(Math.PI * distPerStep / sphereRad);

            var totalDist = 0.0f;
            var totalRad = 0.0f;

            for (var i = 0; i < numSteps; i++)
            {
                totalDist += distPerStep;
                totalRad += radiansPerStep;

                var rad = (int)Math.Ceiling(totalRad);
                var angle = rad * 2;

                var frame = new AFrame();

                for (var j = 0; j < rad; j++)
                {
                    SpherePath.SetCheckPos(SpherePath.CurPos, SpherePath.CurCell);
                    frame.set_heading(angle * j);
                    var offset = frame.get_vector_heading() * totalDist;
                    SpherePath.GlobalOffset = AdjustOffset(offset);
                    if (SpherePath.GlobalOffset.Length() >= PhysicsGlobals.EPSILON)
                    {
                        SpherePath.AddOffsetToCheckPos(SpherePath.GlobalOffset);

                        // possibly reset by AdjustOffset
                        CollisionInfo.SlidingNormalValid = false;
                        CollisionInfo.ContactPlaneValid = false;
                        CollisionInfo.ContactPlaneIsWater = false;

                        transitionState = TransitionalInsert(3);
                        transitionState = ValidatePlacementTransition(transitionState, ref redo);

                        if (transitionState == TransitionState.OK) return true;
                    }
                }
            }
            return false;
        }

        public bool FindPlacementPosition()
        {
            SpherePath.SetCheckPos(SpherePath.CurPos, SpherePath.CurCell);
            SpherePath.InsertType = InsertType.InitialPlacement;
            var transitionState = new TransitionState();

            if (SpherePath.CheckCell != null)
            {
                transitionState = InsertIntoCell(SpherePath.CheckCell, 3);
                if (transitionState == TransitionState.OK)
                    transitionState = CheckOtherCells(SpherePath.CheckCell);
            }
            else
                transitionState = TransitionState.Collided;

            if (ValidatePlacement(transitionState, true) != TransitionState.OK)
                return false;

            SpherePath.InsertType = InsertType.Placement;
            if (!FindPlacementPos()) return false;

            if (!ObjectInfo.StepDown)
                return ValidatePlacement(TransitionState.OK, true) == TransitionState.OK;

            SpherePath.WalkableAllowance = PhysicsGlobals.LandingZ;
            SpherePath.SaveCheckPos();

            SpherePath.Backup = SpherePath.InsertType;
            SpherePath.InsertType = InsertType.Transition;

            var globSphere = SpherePath.GlobalSphere[0];
            var stepDownHeight = ObjectInfo.StepDownHeight;

            if (SpherePath.NumSphere < 2 && globSphere.Radius * 2 < stepDownHeight)
                stepDownHeight = globSphere.Radius * 0.5f;

            if (stepDownHeight < globSphere.Radius * 2)
            {
                if (!StepDown(stepDownHeight, PhysicsGlobals.LandingZ))
                {
                    SpherePath.RestoreCheckPos();
                    CollisionInfo.ContactPlaneValid = false;
                    CollisionInfo.ContactPlaneIsWater = false;
                }
            }
            else
            {
                stepDownHeight *= 0.5f;
                if (!StepDown(stepDownHeight, PhysicsGlobals.LandingZ) && !StepDown(stepDownHeight, PhysicsGlobals.LandingZ))
                {
                    SpherePath.RestoreCheckPos();
                    CollisionInfo.ContactPlaneValid = false;
                    CollisionInfo.ContactPlaneIsWater = false;
                }
            }

            SpherePath.InsertType = SpherePath.Backup;
            SpherePath.Walkable = null;

            return ValidatePlacement(TransitionState.OK, true) == TransitionState.OK;
        }

        public bool FindTransitionalPosition()
        {
            if (SpherePath.BeginCell == null) return false;

            var transitionState = TransitionState.OK;
            var offset = Vector3.Zero;
            var offsetPerStep = Vector3.Zero;
            var numSteps = 0;

            CalcNumSteps(ref offset, ref offsetPerStep, ref numSteps);  // restructure as retval?

            if (ObjectInfo.State.HasFlag(ObjectInfoState.FreeRotate))
                SpherePath.CurPos.Frame.Rotate(SpherePath.EndPos.Frame.Orientation);

            SpherePath.SetCheckPos(SpherePath.CurPos, SpherePath.CurCell);

            var redo = 0;
            if (numSteps <= 0)
            {
                if (!ObjectInfo.State.HasFlag(ObjectInfoState.FreeRotate))  // ?
                    SpherePath.CurPos.Frame.Rotate(SpherePath.EndPos.Frame.Orientation);

                SpherePath.CellArrayValid = true;
                SpherePath.HitsInteriorCell = false;

                CellArray.find_cell_list(SpherePath);
                return true;
            }

            for (var step = 0; step < numSteps; step++)
            {
                if (ObjectInfo.State.HasFlag(ObjectInfoState.IsViewer))
                {
                    var lastStep = numSteps - 1;

                    if (step == lastStep)
                    {
                        var offsetLen = offset.Length();
                        if (offsetLen > PhysicsGlobals.EPSILON)
                        {
                            redo = lastStep;
                            offsetPerStep = offset * (offsetLen - SpherePath.LocalSphere[0].Radius * lastStep) / offsetLen;
                        }
                    }
                }
                SpherePath.GlobalOffset = AdjustOffset(offsetPerStep);
                if (!ObjectInfo.State.HasFlag(ObjectInfoState.IsViewer))
                {
                    if (SpherePath.GlobalOffset.LengthSquared() < PhysicsGlobals.EPSILON * PhysicsGlobals.EPSILON)
                    {
                        return (step != 0 && transitionState == TransitionState.OK);
                    }
                }
                if (!ObjectInfo.State.HasFlag(ObjectInfoState.FreeRotate))
                {
                    redo = step + 1;
                    var delta = (float)redo / numSteps;
                    SpherePath.CheckPos.Frame.InterpolateRotation(SpherePath.BeginPos.Frame, SpherePath.EndPos.Frame, delta);
                }

                CollisionInfo.SlidingNormalValid = false;
                CollisionInfo.ContactPlaneValid = false;
                CollisionInfo.ContactPlaneIsWater = false;

                if (SpherePath.InsertType != InsertType.Transition)
                {
                    var insert = TransitionalInsert(3);
                    transitionState = ValidatePlacementTransition(insert, ref redo);

                    if (transitionState == TransitionState.OK)
                        return true;

                    if (!SpherePath.PlacementAllowsSliding)
                        return false;

                    SpherePath.AddOffsetToCheckPos(SpherePath.GlobalOffset);
                }
                else
                {
                    SpherePath.CellArrayValid = false;

                    SpherePath.CheckPos.Frame.Origin += SpherePath.GlobalOffset;
                    SpherePath.CacheGlobalSphere(SpherePath.GlobalOffset);

                    transitionState = ValidateTransition(TransitionalInsert(3), ref redo);

                    if (CollisionInfo.FramesStationaryFall > 0) break;
                }

                if (CollisionInfo.CollisionNormalValid && ObjectInfo.State.HasFlag(ObjectInfoState.PathClipped)) break;
            }

            return transitionState == TransitionState.OK;
        }

        public bool FindValidPosition()
        {
            if (SpherePath.InsertType == InsertType.Transition)
                return FindTransitionalPosition();
            else
                return FindPlacementPosition();
        }

        public void Init()
        {
            ObjectInfo = new ObjectInfo();
            SpherePath = new SpherePath();
            CollisionInfo = new CollisionInfo();
            CellArray = new CellArray();
            NewCellPtr = new ObjCell();
        }

        public void InitContactPlane(int cellID, Plane contactPlane, bool isWater)
        {
            InitLastKnownContactPlane(cellID, contactPlane, isWater);

            CollisionInfo.ContactPlaneValid = true;
            CollisionInfo.ContactPlane = contactPlane;
            CollisionInfo.ContactPlaneIsWater = isWater;
            CollisionInfo.ContactPlaneCellID = cellID;
        }

        public void InitLastKnownContactPlane(int cellID, Plane contactPlane, bool isWater)
        {
            CollisionInfo.LastKnownContactPlaneValid = true;
            CollisionInfo.LastKnownContactPlane = contactPlane;
            CollisionInfo.LastKnownContactPlaneIsWater = isWater;
            CollisionInfo.LastKnownContactPlaneCellID = cellID;
        }

        public void InitObject(PhysicsObj obj, ObjectInfoState objectState)
        {
            ObjectInfo.Init(obj, objectState);
        }

        public void InitPath(ObjCell beginCell, Position beginPos, Position endPos)
        {
            SpherePath.InitPath(beginCell, beginPos, endPos);
        }

        public void InitSlidingNormal(Vector3 normal)
        {
            CollisionInfo.SlidingNormalValid = true;
            CollisionInfo.SlidingNormal = new Vector3(normal.X, normal.Y, 0);

            if (CollisionInfo.NormalizeCheckSmall(ref CollisionInfo.SlidingNormal))
                CollisionInfo.SlidingNormal = Vector3.Zero;
        }

        public void InitSphere(int numSphere, Sphere sphere, float scale)
        {
            SpherePath.InitSphere(numSphere, new List<Sphere>() { sphere }, scale);
        }

        public TransitionState InsertIntoCell(ObjCell cell, int num_insertion_attempts)
        {
            if (cell == null)
                return TransitionState.Collided;

            var transitionState = TransitionState.OK;

            for (var i = 0; i < num_insertion_attempts; i++)
            {
                transitionState = cell.FindCollisions(this);

                switch (transitionState)
                {
                    case TransitionState.OK:
                    case TransitionState.Collided:
                        return transitionState;

                    case TransitionState.Slid:
                        CollisionInfo.ContactPlaneValid = false;
                        CollisionInfo.ContactPlaneIsWater = false;
                        break;
                }
            }
            return TransitionState.OK;
        }

        public static Transition MakeTransition()
        {
            if (TransitionLevel >= MaxTransitionLevel) return null;

            var transition = new Transition();
            transition.Init();

            if (Transitions.Count < TransitionLevel)
                Transitions.Add(transition);
            else
                Transitions[TransitionLevel] = transition;

            TransitionLevel++;

            return transition;
        }

        public TransitionState PlacementInsert()
        {
            if (SpherePath.CheckCell == null) return TransitionState.Collided;

            var checkCell = SpherePath.CheckCell;
            var transitState = InsertIntoCell(checkCell, 3);

            if (transitState == TransitionState.OK)
                transitState = CheckOtherCells(checkCell);

            return transitState;
        }

        public bool StepDown(float stepDownHeight, float zVal)
        {
            SpherePath.NegPolyHit = false;
            SpherePath.StepDown = true;

            SpherePath.StepDownAmt = stepDownHeight;
            SpherePath.WalkInterp = 1.0f;

            if (!SpherePath.StepUp)
            {
                SpherePath.CellArrayValid = false;

                var offset = new Vector3(0, 0, -stepDownHeight);

                SpherePath.CheckPos.Frame.Origin.Z -= stepDownHeight;
                SpherePath.CacheGlobalSphere(offset);
            }

            var transitionState = TransitionalInsert(5);
            SpherePath.StepDown = false;

            if (transitionState == TransitionState.OK && CollisionInfo.ContactPlaneValid && CollisionInfo.ContactPlane.Normal.Z >= zVal &&
                (!ObjectInfo.State.HasFlag(ObjectInfoState.EdgeSlide) || SpherePath.StepUp || CheckWalkable(zVal)))
            {
                SpherePath.Backup = SpherePath.InsertType;
                SpherePath.InsertType = InsertType.Placement;

                transitionState = TransitionalInsert(1);

                SpherePath.InsertType = SpherePath.Backup;
                return (transitionState == TransitionState.OK);
            }

            return false;
        }

        public bool StepUp(Vector3 collisionNormal)
        {
            CollisionInfo.ContactPlaneValid = false;
            CollisionInfo.ContactPlaneIsWater = false;

            SpherePath.StepUp = true;
            SpherePath.StepUpNormal = collisionNormal;

            var stepDownHeight = 0.039999999f;  // set global

            var zLandingValue = PhysicsGlobals.LandingZ;

            if (ObjectInfo.State.HasFlag(ObjectInfoState.OnWalkable))
            {
                zLandingValue = ObjectInfo.GetWalkableZ();
                stepDownHeight = ObjectInfo.StepUpHeight;
            }

            SpherePath.WalkableAllowance = zLandingValue;
            SpherePath.BackupCell = SpherePath.CheckCell;
            SpherePath.BackupCheckPos = SpherePath.CheckPos;

            var stepDown = StepDown(stepDownHeight, zLandingValue);

            SpherePath.StepUp = false;
            SpherePath.Walkable = null;

            if (!stepDown)
            {
                SpherePath.RestoreCheckPos();
                return false;
            }
            else
                return true;
        }

        public TransitionState TransitionalInsert(int num_insertion_attempts)
        {
            if (SpherePath.CheckCell == null)
                return TransitionState.OK;

            if (num_insertion_attempts <= 0)
                return TransitionState.Invalid;

            var transitState = TransitionState.Invalid;

            for (var i = 0; i < num_insertion_attempts; i++)
            {
                transitState = InsertIntoCell(SpherePath.CheckCell, num_insertion_attempts);

                switch (transitState)
                {
                    case TransitionState.OK:

                        transitState = CheckOtherCells(SpherePath.CheckCell);

                        if (transitState != TransitionState.OK)
                            SpherePath.NegPolyHit = false;

                        if (transitState == TransitionState.Collided)
                            return transitState;

                        break;

                    case TransitionState.Collided:

                        return transitState;

                    case TransitionState.Adjusted:

                        SpherePath.NegPolyHit = false;
                        break;

                    case TransitionState.Slid:

                        CollisionInfo.ContactPlaneValid = false;
                        CollisionInfo.ContactPlaneIsWater = false;
                        SpherePath.NegPolyHit = false;
                        break;
                }

                if (transitState == TransitionState.OK)
                {
                    if (!SpherePath.Collide)
                    {
                        if (SpherePath.NegPolyHit && !SpherePath.StepDown && !SpherePath.StepUp)
                        {
                            SpherePath.NegPolyHit = false;

                            if (SpherePath.NegStepUp)
                            {
                                if (!StepUp(SpherePath.NegCollisionNormal))
                                    transitState = SpherePath.StepUpSlide(this);
                            }
                            else
                                transitState = SpherePath.GlobalSphere[0].SlideSphere(this,
                                    SpherePath.NegCollisionNormal, SpherePath.GlobalCurrCenter[0].Center);
                        }
                        else
                        {
                            if (CollisionInfo.ContactPlaneValid || !ObjectInfo.State.HasFlag(ObjectInfoState.Contact) ||
                                SpherePath.StepDown || SpherePath.CheckCell == null || ObjectInfo.StepDown)
                            {
                                return TransitionState.OK;
                            }

                            var zVal = PhysicsGlobals.LandingZ;
                            var stepDownHeight = 0.039999999f;  // set global

                            if (ObjectInfo.State.HasFlag(ObjectInfoState.OnWalkable))
                            {
                                zVal = ObjectInfo.GetWalkableZ();
                                stepDownHeight = ObjectInfo.StepDownHeight;
                            }
                            SpherePath.WalkableAllowance = zVal;
                            SpherePath.SaveCheckPos();

                            var radsum = SpherePath.GlobalSphere[0].Radius * 2;
                            if (SpherePath.NumSphere < 2)
                            {
                                if (radsum < stepDownHeight)
                                    stepDownHeight = SpherePath.GlobalSphere[0].Radius * 0.5f;
                            }

                            if (radsum < stepDownHeight)
                            {
                                stepDownHeight *= 0.5f;
                                if (StepDown(stepDownHeight, zVal) || StepDown(stepDownHeight, zVal))   // double step?
                                {
                                    SpherePath.Walkable = null;
                                    return TransitionState.OK;
                                }
                            }

                            if (StepDown(stepDownHeight, zVal))
                            {
                                SpherePath.Walkable = null;
                                return TransitionState.OK;
                            }

                            if (EdgeSlide(ref transitState, stepDownHeight, zVal))
                                return transitState;
                        }
                    }
                }
                else
                {
                    var reset = false;
                    SpherePath.Collide = false;
                    if (CollisionInfo.ContactPlaneValid && CheckWalkable(PhysicsGlobals.LandingZ))
                    {
                        SpherePath.Backup = SpherePath.InsertType;
                        SpherePath.InsertType = InsertType.Placement;

                        transitState = TransitionalInsert(num_insertion_attempts);

                        SpherePath.InsertType = SpherePath.Backup;

                        if (transitState != TransitionState.OK)
                        {
                            transitState = TransitionState.OK;
                            reset = true;
                        }
                    }
                    else
                        reset = true;

                    SpherePath.Walkable = null;

                    if (!reset) return transitState;

                    SpherePath.RestoreCheckPos();

                    CollisionInfo.ContactPlaneValid = false;
                    CollisionInfo.ContactPlaneIsWater = false;

                    if (CollisionInfo.LastKnownContactPlaneValid)
                    {
                        CollisionInfo.LastKnownContactPlaneValid = false;
                        ObjectInfo.StopVelocity();
                    }
                    else
                        CollisionInfo.SetCollisionNormal(SpherePath.StepUpNormal);

                    return TransitionState.Collided;
                }
            }
            return transitState;
        }

        public TransitionState ValidatePlacement(TransitionState transitionState, bool adjust)
        {
            if (SpherePath.CheckCell == null) return TransitionState.Collided;

            switch (transitionState)
            {
                case TransitionState.OK:
                    SpherePath.CurPos = SpherePath.CheckPos;
                    SpherePath.CurCell = SpherePath.CheckCell;
                    SpherePath.CacheGlobalCurrCenter();
                    break;

                case TransitionState.Adjusted:
                case TransitionState.Slid:
                    if (adjust)
                        return ValidatePlacement(PlacementInsert(), false);
                    break;
            }
            return transitionState;
        }

        public TransitionState ValidatePlacementTransition(TransitionState transitionState, ref int redo)
        {
            redo = 0;

            if (SpherePath.CheckCell == null) return TransitionState.Collided;

            switch (transitionState)
            {
                case TransitionState.OK:
                    SpherePath.CurPos = SpherePath.CheckPos;
                    SpherePath.CurCell = SpherePath.CheckCell;
                    SpherePath.CacheGlobalCurrCenter();
                    break;

                case TransitionState.Collided:
                case TransitionState.Adjusted:
                case TransitionState.Slid:
                    if (SpherePath.PlacementAllowsSliding) CollisionInfo.Init();
                    break;
            }
            return transitionState;
        }

        public TransitionState ValidateTransition(TransitionState transitionState, ref int redo)
        {
            int _redo = 1;
            Plane contactPlane = new Plane();

            if (transitionState != TransitionState.OK || SpherePath.CheckPos.Equals(SpherePath.CurPos))
            {
                _redo = 0;
                if (transitionState != TransitionState.OK)
                {
                    if (transitionState != TransitionState.Invalid)
                    {
                        if (CollisionInfo.LastKnownContactPlaneValid)
                        {
                            ObjectInfo.StopVelocity();
                            var angle = Vector3.Dot(CollisionInfo.LastKnownContactPlane.Normal, SpherePath.GlobalCurrCenter[0].Center);
                            if (SpherePath.GlobalSphere[0].Radius + PhysicsGlobals.EPSILON > Math.Abs(angle))
                            {
                                CollisionInfo.SetContactPlane(CollisionInfo.LastKnownContactPlane, CollisionInfo.LastKnownContactPlaneIsWater);
                                CollisionInfo.ContactPlaneCellID = CollisionInfo.LastKnownContactPlaneCellID;

                                if (ObjectInfo.State.HasFlag(ObjectInfoState.OnWalkable)) _redo = 1;
                            }
                        }
                        if (!CollisionInfo.CollisionNormalValid)
                        {
                            contactPlane.Normal = Vector3.UnitZ;
                            CollisionInfo.SetCollisionNormal(contactPlane.Normal);
                        }
                        SpherePath.SetCheckPos(SpherePath.CurPos, SpherePath.CurCell);
                        BuildCellArray();
                        transitionState = TransitionState.OK;
                    }
                }
                else
                    SetCurrentCheckPos();
            }
            else
                SetCurrentCheckPos();

            if (CollisionInfo.CollisionNormalValid)
                CollisionInfo.SetSlidingNormal(CollisionInfo.CollisionNormal);

            if (!ObjectInfo.State.HasFlag(ObjectInfoState.IsViewer))
            {
                if (ObjectInfo.Object.State.HasFlag(PhysicsState.Gravity))
                {
                    if (_redo > 0)
                    {
                        if (CollisionInfo.FramesStationaryFall > 0)
                        {
                            if (CollisionInfo.FramesStationaryFall > 1)
                            {
                                CollisionInfo.FramesStationaryFall = 3;
                                contactPlane.Normal = Vector3.UnitZ;
                                contactPlane.D = SpherePath.GlobalSphere[0].Radius - SpherePath.GlobalSphere[0].Center.Z;

                                CollisionInfo.SetContactPlane(contactPlane, false);
                                CollisionInfo.ContactPlaneCellID = SpherePath.CheckPos.ObjCellID;

                                if (!ObjectInfo.State.HasFlag(ObjectInfoState.Contact))
                                {
                                    CollisionInfo.SetCollisionNormal(contactPlane.Normal);
                                    CollisionInfo.CollidedWithEnvironment = true;
                                }
                            }
                            else
                                CollisionInfo.FramesStationaryFall = 2;
                        }
                        else
                            CollisionInfo.FramesStationaryFall = 1;
                    }
                    else
                        CollisionInfo.FramesStationaryFall = 0;
                }
            }

            CollisionInfo.LastKnownContactPlaneValid = CollisionInfo.ContactPlaneValid;

            if (CollisionInfo.ContactPlaneValid)
            {
                CollisionInfo.LastKnownContactPlane = CollisionInfo.ContactPlane;
                CollisionInfo.LastKnownContactPlaneCellID = CollisionInfo.ContactPlaneCellID;
                CollisionInfo.LastKnownContactPlaneIsWater = CollisionInfo.ContactPlaneIsWater;

                ObjectInfo.State |= ObjectInfoState.Contact;

                if (ObjectInfo.IsValidWalkable(CollisionInfo.ContactPlane.Normal))
                    ObjectInfo.State |= ObjectInfoState.OnWalkable;
                else
                    ObjectInfo.State &= ~ObjectInfoState.OnWalkable;
            }
            else
                ObjectInfo.State &= ~(ObjectInfoState.Contact | ObjectInfoState.OnWalkable);

            return transitionState;
        }

        public void SetCurrentCheckPos()
        {
            SpherePath.CurPos = SpherePath.CheckPos;
            SpherePath.CurCell = SpherePath.CheckCell;
            SpherePath.CacheGlobalCurrCenter();

            SpherePath.SetCheckPos(SpherePath.CurPos, SpherePath.CurCell);
        }
    }
}