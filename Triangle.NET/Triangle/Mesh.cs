﻿// -----------------------------------------------------------------------
// <copyright file="Mesh.cs">
// Original Triangle code by Jonathan Richard Shewchuk, http://www.cs.cmu.edu/~quake/triangle.html
// Triangle.NET code by Christian Woltering, http://home.edo.tu-dortmund.de/~woltering/triangle/
// </copyright>
// -----------------------------------------------------------------------

namespace TriangleNet
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using TriangleNet.Data;
    using TriangleNet.Log;
    using TriangleNet.IO;
    using TriangleNet.Algorithm;
    using TriangleNet.Smoothing;

    /// <summary>
    /// Mesh data structure.
    /// </summary>
    public class Mesh
    {
        #region Variables

        ILog<SimpleLogItem> logger;

        Quality quality;
        Sampler sampler;

        // Variable that maintains the stack of recently flipped triangles.
        List<FlipStacker> flipstackers;
        FlipStacker lastflip;

        // Using hashsets for memory management should quite fast.
        internal Dictionary<int, Triangle> triangles;
        internal Dictionary<int, Subseg> subsegs;
        internal Dictionary<int, Vertex> vertices;

		// TODO: Check if custom hashmap implementation could be faster.

        internal List<Point2> holes;
        internal List<Region> regions;

        internal List<Triangle> viri;

        // Other variables.
        internal double xmin, xmax, ymin, ymax;   // x and y bounds.
        internal int invertices;            // Number of input vertices.
        internal int inelements;            // Number of input triangles.
        internal int insegments;            // Number of input segments.
        internal int undeads;    // Number of input vertices that don't appear in the mesh.
        internal int edges;                // Number of output edges.
        internal int mesh_dim;              // Dimension (ought to be 2).
        internal int nextras;               // Number of attributes per vertex.
        internal int eextras;               // Number of attributes per triangle.
        internal int hullsize;             // Number of edges in convex hull.
        internal int steinerleft;           // Number of Steiner points not yet used.
        internal bool checksegments;        // Are there segments in the triangulation yet?
        internal bool checkquality;         // Has quality triangulation begun yet?

        // Triangular bounding box vertices.
        internal Vertex infvertex1, infvertex2, infvertex3;

        // The 'triangle' that occupies all of "outer space."
        internal static Triangle dummytri;

        // The omnipresent subsegment. Referenced by any triangle or
        // subsegment that isn't really connected to a subsegment at
        // that location.
        internal static Subseg dummysub;

        // Pointer to a recently visited triangle. Improves point location if
        // proximate vertices are inserted sequentially.
        internal Otri recenttri;

        static FlipStacker dummyflip;

        #endregion

        /// <summary>
        /// Gets the number of input vertices.
        /// </summary>
        public int NumberOfInputPoints { get { return invertices; } }

        public Mesh()
        {
            logger = SimpleLog.Instance;

            Behavior.Init();

            vertices = new Dictionary<int, Vertex>();
            triangles = new Dictionary<int, Triangle>();
            subsegs = new Dictionary<int, Subseg>();

            viri = new List<Triangle>();
            flipstackers = new List<FlipStacker>();

            holes = new List<Point2>();
            regions = new List<Region>();

            quality = new Quality(this);

            sampler = new Sampler();

            Primitives.ExactInit();

            if (dummytri == null)
            {
                DummyInit();
            }
        }

        /// <summary>
        /// Load a mesh from file (.node/poly and .ele).
        /// </summary>
        public void Load(string inputfile)
        {
            MeshData input = FileReader.ReadFile(inputfile, true);

            this.Load(input);
        }

        /// <summary>
        /// Reconstructs a mesh from raw input data.
        /// </summary>
        public void Load(MeshData input)
        {
            if (input.Triangles == null)
            {
                throw new ArgumentException("The input data contains no triangles.");
            }

            // Clear all data structures / reset hash seeds
            this.ResetData();

            if (input.Segments != null)
            {
                Behavior.Poly = true;
            }

            if (input.EdgeMarkers != null)
            {
                Behavior.UseBoundaryMarkers = true;
            }

            if (input.TriangleAreas != null)
            {
                Behavior.VarArea = true;
            }

            if (!Behavior.Poly)
            {
                // Be careful not to allocate space for element area constraints that
                // will never be assigned any value (other than the default -1.0).
                Behavior.VarArea = false;

                // Be careful not to add an extra attribute to each element unless the
                // input supports it (PSLG in, but not refining a preexisting mesh).
                Behavior.RegionAttrib = false;
            }

            TransferNodes(input);

            // Read and reconstruct a mesh.
            hullsize = DataReader.Reconstruct(this, input);
        }

        /// <summary>
        /// Triangulate given input file (.node or .poly).
        /// </summary>
        /// <param name="input"></param>
        public void Triangulate(string inputFile)
        {
            MeshData input = FileReader.ReadFile(inputFile);

            this.Triangulate(input);
        }

        /// <summary>
        /// Triangulate given input data.
        /// </summary>
        /// <param name="input"></param>
        public void Triangulate(MeshData input)
        {
            ResetData();

            if (input.Segments != null)
            {
                Behavior.Poly = true;
            }

            if (input.EdgeMarkers != null)
            {
                Behavior.UseBoundaryMarkers = true;
            }

            if (!Behavior.Poly)
            {
                // Be careful not to allocate space for element area constraints that
                // will never be assigned any value (other than the default -1.0).
                Behavior.VarArea = false;

                // Be careful not to add an extra attribute to each element unless the
                // input supports it (PSLG in, but not refining a preexisting mesh).
                Behavior.RegionAttrib = false;
            }

            steinerleft = Behavior.Steiner;

            TransferNodes(input);

            hullsize = Delaunay(); // Triangulate the vertices.

            // Ensure that no vertex can be mistaken for a triangular bounding
            //   box vertex in insertvertex().
            infvertex1 = null;
            infvertex2 = null;
            infvertex3 = null;

            if (Behavior.UseSegments)
            {
                // Segments will be introduced next.
                checksegments = true;

                // Insert PSLG segments and/or convex hull segments.
                FormSkeleton(input);
            }

            if (Behavior.Poly && (triangles.Count > 0))
            {
                if (input.Holes != null)
                {
                    // Copy holes
                    for (int i = 0; i < input.Holes.Length; i++)
                    {
                        holes.Add(new Point2(input.Holes[i][0], input.Holes[i][1]));
                    }
                }

                if (input.Regions != null)
                {
                    // Copy regions
                    for (int i = 0; i < input.Regions.Length; i++)
                    {
                        regions.Add(new Region(input.Regions[i]));
                    }
                }

                // Carve out holes and concavities.
                Carver c = new Carver(this);
                c.CarveHoles();
            }
            else
            {
                // Without a PSLG, there can be no holes or regional attributes
                // or area constraints. The following are set to zero to avoid
                // an accidental free() later.
                //
                // TODO: -
                holes.Clear();
                regions.Clear();
            }

            if (Behavior.Quality && (triangles.Count > 0))
            {
                quality.EnforceQuality();           // Enforce angle and area constraints.
            }

            // Calculate the number of edges.
            edges = (3 * triangles.Count + hullsize) / 2;
        }

        /// <summary>
        /// Refines the current mesh by finding the maximum triangle area and setting
        /// the a global area constraint to half its size.
        /// </summary>
        public void Refine(bool halfArea)
        {
            if (halfArea)
            {
                double tmp, maxArea = 0;

                foreach (var t in this.triangles.Values)
                {
                    tmp = (t.vertices[2].pt.X - t.vertices[0].pt.X) * (t.vertices[1].pt.Y - t.vertices[0].pt.Y) -
                        (t.vertices[1].pt.X - t.vertices[0].pt.X) * (t.vertices[2].pt.Y - t.vertices[0].pt.Y);

                    tmp = Math.Abs(tmp) / 2.0;

                    if (tmp > maxArea)
                    {
                        maxArea = tmp;
                    }
                }

                this.Refine(maxArea / 2);
            }
            else
            {
                Refine();
            }
        }

        /// <summary>
        /// Refines the current mesh by setting a global area constraint.
        /// </summary>
        /// <param name="areaConstraint">Global area constraint.</param>
        public void Refine(double areaConstraint)
        {
            Behavior.FixedArea = true;
            Behavior.MaxArea = areaConstraint;

            this.Refine();

            // Reset option for sanity
            Behavior.FixedArea = false;
            Behavior.MaxArea = -1.0;
        }

        /// <summary>
        /// Refines the current mesh.
        /// </summary>
        public void Refine()
        {
            inelements = triangles.Count;
            invertices = vertices.Count;

            // TODO: Set all vertex types to input (i.e. NOT free)?

            if (Behavior.Poly)
            {
                if (Behavior.UseSegments)
                {
                    insegments = subsegs.Count;
                }
                else
                {
                    insegments = (int)hullsize;
                }
            }

            Reset();

            steinerleft = Behavior.Steiner;

            // Ensure that no vertex can be mistaken for a triangular bounding
            // box vertex in insertvertex().
            infvertex1 = null;
            infvertex2 = null;
            infvertex3 = null;

            if (Behavior.UseSegments)
            {
                checksegments = true;
            }

            // TODO
            //holes.Clear();
            //regions.Clear();

            if (triangles.Count > 0)
            {
                // Enforce angle and area constraints.
                quality.EnforceQuality();
            }

            // Calculate the number of edges.
            edges = (3 * triangles.Count + hullsize) / 2;
        }

        /// <summary>
        /// Smooth the current mesh.
        /// </summary>
        public void Smooth()
        {
            //ISmoother smoother = new CvdSmoother(this);
            //smoother.Smooth();
        }

        /// <summary>
        /// Check mesh consistency and (constrained) Delaunay property.
        /// </summary>
        public void Check()
        {
            quality.CheckMesh();
            quality.CheckDelaunay();
        }

        /// <summary>
        /// Returns the raw mesh data.
        /// </summary>
        /// <returns>Mesh data.</returns>
        public MeshData GetMeshData()
        {
            return GetMeshData(true, true, true);
        }

        /// <summary>
        /// Returns the raw mesh data.
        /// </summary>
        /// <param name="writeElements">Write elements to output.</param>
        /// <param name="writeEdges">Write edges to output.</param>
        /// <param name="writeNeighbors">Write neighbour information to output.</param>
        /// <returns>Mesh data.</returns>
        public MeshData GetMeshData(bool writeElements, bool writeEdges, bool writeNeighbors)
        {
            MeshData output = new MeshData();

            if (Behavior.UseSegments)
            {
                //output.NumberOfSegments = subsegs.Count;
            }
            else
            {
                //output.NumberOfSegments = (int)hullsize;
            }

            // Numbers the vertices too.
            DataWriter.WriteNodes(this, output);

            if (writeElements)
            {
                DataWriter.WriteElements(this, output);
            }

            // The -c switch (convex switch) causes a PSLG to be written
            // even if none was read.
            if (Behavior.Poly || Behavior.Convex)
            {
                DataWriter.WritePoly(this, output);
            }

            if (writeEdges)
            {
                DataWriter.WriteEdges(this, output);
            }

            if (writeElements && writeNeighbors)
            {
                DataWriter.WriteNeighbors(this, output);
            }

            return output;
        }

        #region Options

        /// <summary>
        /// Set options for mesh generation.
        /// </summary>
        /// <param name="option">Mesh gerneration option.</param>
        /// <param name="value">New option value.</param>
        public void SetOption(Options option, bool value)
        {
            if (option == Options.ConformingDelaunay)
            {
                Behavior.ConformDel = value;
                Behavior.Quality = value; // TODO: ok?
                return;
            }
            else if (option == Options.BoundaryMarkers)
            {
                Behavior.UseBoundaryMarkers = value;
                return;
            }
            else if (option == Options.Quality)
            {
                Behavior.Quality = value;

                if (value)
                {
                    Behavior.MinAngle = 20.0;
                    Behavior.MaxAngle = 140.0;
                    UpdateOptions();
                }

                return;
            }
            else if (option == Options.Convex)
            {
                Behavior.Convex = value;
                return;
            }

            logger.Warning("Invalid option value.", "Mesh.SetOption(bool)");
        }

        /// <summary>
        /// Set options for mesh generation.
        /// </summary>
        /// <param name="option">Mesh gerneration option.</param>
        /// <param name="value">New option value.</param>
        public void SetOption(Options option, double value)
        {

            if (option == Options.MinAngle)
            {
                Behavior.MinAngle = value;
                Behavior.Quality = (value >= 0); // TODO: ok?
                UpdateOptions();
                return;
            }
            else if (option == Options.MaxAngle)
            {
                Behavior.MaxAngle = value;
                Behavior.Quality = (value >= 0); // TODO: ok?
                UpdateOptions();
                return;
            }
            else if (option == Options.MaxArea)
            {
                Behavior.MaxArea = value;
                Behavior.FixedArea = true;
                Behavior.Quality = (value >= 0); // TODO: ok?
                return;
            }

            logger.Warning("Invalid option value.", "Mesh.SetOption(double)");
        }

        /// <summary>
        /// Set options for mesh generation.
        /// </summary>
        /// <param name="option">Mesh gerneration option.</param>
        /// <param name="value">New option value.</param>
        public void SetOption(Options option, int value)
        {
            if (option == Options.NoBisect)
            {
                Behavior.NoBisect = value;
                return;
            }
            else if (option == Options.SteinerPoints)
            {
                Behavior.Steiner = value;
                return;
            }
            else if (option == Options.MinAngle)
            {
                Behavior.MinAngle = value;
                Behavior.Quality = (value >= 0); // TODO: ok?
                UpdateOptions();
                return;
            }
            else if (option == Options.MaxAngle)
            {
                Behavior.MaxAngle = value;
                Behavior.Quality = (value >= 0); // TODO: ok?
                UpdateOptions();
                return;
            }
            else if (option == Options.MaxArea)
            {
                Behavior.MaxArea = value;
                Behavior.Quality = (value >= 0); // TODO: ok?
                Behavior.FixedArea = true;
                return;
            }

            logger.Warning("Invalid option value.", "Mesh.SetOption(int)");
        }

        /// <summary>
        /// Keeps options synchronized.
        /// </summary>
        private void UpdateOptions()
        {
            Behavior.UseSegments = Behavior.Poly || Behavior.Quality || Behavior.Convex;
            Behavior.GoodAngle = Math.Cos(Behavior.MinAngle * Math.PI / 180.0);
            Behavior.MaxGoodAngle = Math.Cos(Behavior.MaxAngle * Math.PI / 180.0);

            if (Behavior.GoodAngle == 1.0)
            {
                Behavior.Offconstant = 0.0;
            }
            else
            {
                Behavior.Offconstant = 0.475 * Math.Sqrt((1.0 + Behavior.GoodAngle) / (1.0 - Behavior.GoodAngle));
            }

            Behavior.GoodAngle *= Behavior.GoodAngle;
        }

        #endregion

        #region Misc

        /// <summary>
        /// Form a Delaunay triangulation.
        /// </summary>
        /// <returns>The number of points on the hull.</returns>
        int Delaunay()
        {
            int hulledges = 0;

            eextras = 0;

            if (Behavior.Algorithm == TriangulationAlgorithm.Dwyer)
            {
                Dwyer alg = new Dwyer();
                hulledges = alg.Triangulate(this);
            }
            else if (Behavior.Algorithm == TriangulationAlgorithm.SweepLine)
            {
                SweepLine alg = new SweepLine();
                hulledges = alg.Triangulate(this);
            }
            else
            {
                Incremental alg = new Incremental();
                hulledges = alg.Triangulate(this);
            }

            if (triangles.Count == 0)
            {
                // The input vertices were all collinear, 
                // so there are no triangles.
                return 0;
            }

            return hulledges;
        }

        /// <summary>
        /// Reset all the mesh data. This method will also wipe 
        /// out all mesh data.
        /// </summary>
        /// <param name="points">The number of input points. Used to initialize 
        /// memory for the mesh data.</param>
        private void ResetData()
        {
            vertices.Clear();
            triangles.Clear();
            subsegs.Clear();

            holes.Clear();
            regions.Clear();

            Triangle.ResetHashSeed(0);
            Vertex.ResetHashSeed(0);
            Subseg.ResetHashSeed(0);

            viri.Clear();
            flipstackers.Clear();

            hullsize = 0;
            xmin = 0;
            xmax = 0;
            ymin = 0;
            ymax = 0;
            edges = 0;

            sampler.Reset();

            Reset();
        }

        /// <summary>
        /// Reset the mesh triangulation state.
        /// </summary>
        private void Reset()
        {
            recenttri.triangle = null; // No triangle has been visited yet.
            undeads = 0;               // No eliminated input vertices yet.
            checksegments = false;     // There are no segments in the triangulation yet.
            checkquality = false;      // The quality triangulation stage has not begun.

            Statistic.InCircleCount = 0;
            Statistic.CounterClockwiseCount = 0;
            Statistic.Orient3dCount = 0;
            Statistic.HyperbolaCount = 0;
            Statistic.CircleTopCount = 0;
            Statistic.CircumcenterCount = 0;
        }

        /// <summary>
        /// Initialize the triangle that fills "outer space" and the omnipresent subsegment.
        /// </summary>
        /// <remarks>
        /// The triangle that fills "outer space," called 'dummytri', is pointed to
        /// by every triangle and subsegment on a boundary (be it outer or inner) of
        /// the triangulation.  Also, 'dummytri' points to one of the triangles on
        /// the convex hull (until the holes and concavities are carved), making it
        /// possible to find a starting triangle for point location.
        //
        /// The omnipresent subsegment, 'dummysub', is pointed to by every triangle
        /// or subsegment that doesn't have a full complement of real subsegments
        /// to point to.
        //
        /// 'dummytri' and 'dummysub' are generally required to fulfill only a few
        /// invariants:  their vertices must remain NULL and 'dummytri' must always
        /// be bonded (at offset zero) to some triangle on the convex hull of the
        /// mesh, via a boundary edge.  Otherwise, the connections of 'dummytri' and
        /// 'dummysub' may change willy-nilly.  This makes it possible to avoid
        /// writing a good deal of special-case code (in the edge flip, for example)
        /// for dealing with the boundary of the mesh, places where no subsegment is
        /// present, and so forth.  Other entities are frequently bonded to
        /// 'dummytri' and 'dummysub' as if they were real mesh entities, with no
        /// harm done.
        /// </remarks>
        private void DummyInit()
        {
            // Set up 'dummytri', the 'triangle' that occupies "outer space."
            dummytri = new Triangle(0);

            // Initialize the three adjoining triangles to be "outer space."  These
            // will eventually be changed by various bonding operations, but their
            // values don't really matter, as long as they can legally be
            // dereferenced.
            dummytri.neighbors[0].triangle = dummytri;
            dummytri.neighbors[1].triangle = dummytri;
            dummytri.neighbors[2].triangle = dummytri;

            if (Behavior.UseSegments)
            {
                // Set up 'dummysub', the omnipresent subsegment pointed to by any
                // triangle side or subsegment end that isn't attached to a real
                // subsegment.
                dummysub = new Subseg();

                // Initialize the two adjoining subsegments to be the omnipresent
                // subsegment.  These will eventually be changed by various bonding
                // operations, but their values don't really matter, as long as they
                // can legally be dereferenced.
                dummysub.subsegs[0].ss = dummysub;
                dummysub.subsegs[1].ss = dummysub;

                // Initialize the three adjoining subsegments of 'dummytri' to be
                // the omnipresent subsegment.
                dummytri.subsegs[0].ss = dummysub;
                dummytri.subsegs[1].ss = dummysub;
                dummytri.subsegs[2].ss = dummysub;
            }

            dummyflip = new FlipStacker();
        }

        /// <summary>
        /// Read the vertices from memory.
        /// </summary>
        /// <param name="data">The input data.</param>
        private void TransferNodes(MeshData data)
        {
            Vertex vertex;
            double x, y;
            int attribs;

            double[][] points = data.Points;
            attribs = data.PointAttributes == null ? 0 : data.PointAttributes.Length;

            this.invertices = data.Points.Length;
            this.mesh_dim = 2;
            this.nextras = attribs;

            if (this.invertices < 3)
            {
                logger.Error("Input must have at least three input vertices.", "MeshReader.TransferNodes()");
                throw new Exception("Input must have at least three input vertices.");
            }

            // Read the vertices.
            for (int i = 0; i < this.invertices; i++)
            {
                vertex = new Vertex(nextras);
                vertex.type = VertexType.InputVertex;
                vertex.mark = 0;
                vertex.ID = vertex.Hash;

                // Read the vertex coordinates.
                x = vertex.pt.X = points[i][0];
                y = vertex.pt.Y = points[i][1];

                // Read the vertex attributes.
                for (int j = 0; j < attribs; j++)
                {
                    //vertexloop.pt.attribs[j] = pointattriblist[i][j];
                }
                if (data.PointMarkers != null)
                {
                    // Read a vertex marker.
                    vertex.mark = data.PointMarkers[i];
                }

                this.vertices.Add(vertex.Hash, vertex);

                // Determine the smallest and largest x and y coordinates.
                if (i == 0)
                {
                    this.xmin = this.xmax = x;
                    this.ymin = this.ymax = y;
                }
                else
                {
                    this.xmin = (x < this.xmin) ? x : this.xmin;
                    this.xmax = (x > this.xmax) ? x : this.xmax;
                    this.ymin = (y < this.ymin) ? y : this.ymin;
                    this.ymax = (y > this.ymax) ? y : this.ymax;
                }
            }
        }

        #endregion

        #region Factory

        /// <summary>
        /// Create a new triangle with orientation zero.
        /// </summary>
        /// <param name="newotri">Reference to the new triangle.</param>
        internal void MakeTriangle(ref Otri newotri)
        {
            Triangle t = new Triangle(eextras);

            newotri.triangle = t;
            newotri.orient = 0;

            triangles.Add(t.Hash, t);
        }

        /// <summary>
        /// Create a new subsegment with orientation zero.
        /// </summary>
        /// <param name="newsubseg">Reference to the new subseg.</param>
        internal void MakeSubseg(ref Osub newsubseg)
        {
            Subseg s = new Subseg();

            newsubseg.ss = s;
            newsubseg.ssorient = 0;

            subsegs.Add(s.Hash, s);
        }
        #endregion

        #region Manipulation

        /// <summary>
        /// Insert a vertex into a Delaunay triangulation, performing flips as necessary 
        /// to maintain the Delaunay property.
        /// </summary>
        /// <remarks>
        /// The point 'newvertex' is located.  If 'searchtri.tri' is not NULL,
        /// the search for the containing triangle begins from 'searchtri'.  If
        /// 'searchtri.tri' is NULL, a full point location procedure is called.
        /// If 'insertvertex' is found inside a triangle, the triangle is split into
        /// three; if 'insertvertex' lies on an edge, the edge is split in two,
        /// thereby splitting the two adjacent triangles into four.  Edge flips are
        /// used to restore the Delaunay property.  If 'insertvertex' lies on an
        /// existing vertex, no action is taken, and the value DUPLICATEVERTEX is
        /// returned.  On return, 'searchtri' is set to a handle whose origin is the
        /// existing vertex.
        ///
        /// InsertVertex() does not use flip() for reasons of speed; some
        /// information can be reused from edge flip to edge flip, like the
        /// locations of subsegments.
        /// </remarks>
        /// <param name="newvertex">The point to be inserted.</param>
        /// <param name="searchtri">The triangle to start the search.</param>
        /// <param name="splitseg">Normally, the parameter 'splitseg' is set to NULL, 
        /// implying that no subsegment should be split.  In this case, if 'insertvertex' 
        /// is found to lie on a segment, no action is taken, and the value VIOLATINGVERTEX 
        /// is returned. On return, 'searchtri' is set to a handle whose primary edge is the 
        /// violated subsegment.
        /// 
        /// If the calling routine wishes to split a subsegment 
        /// by inserting a vertex in it, the parameter 'splitseg' should be that subsegment. 
        /// In this case, 'searchtri' MUST be the triangle handle reached by pivoting
        /// from that subsegment; no point location is done.</param>
        /// <param name="segmentflaws">Flags that indicate whether or not there should
        /// be checks for the creation of encroached subsegments. If a newly inserted 
        /// vertex encroaches upon subsegments, these subsegments are added to the list 
        /// of subsegments to be split if 'segmentflaws' is set.</param>
        /// <param name="triflaws">Flags that indicate whether or not there should be
        /// checks for the creation of bad quality triangles. If bad triangles are 
        /// created, these are added to the queue if 'triflaws' is set.</param>
        /// <returns> If a duplicate vertex or violated segment does not prevent the 
        /// vertex from being inserted, the return value will be ENCROACHINGVERTEX if 
        /// the vertex encroaches upon a subsegment (and checking is enabled), or
        /// SUCCESSFULVERTEX otherwise. In either case, 'searchtri' is set to a handle
        /// whose origin is the newly inserted vertex.</returns>
        internal InsertVertexResult InsertVertex(Vertex newvertex, ref Otri searchtri, ref Osub splitseg, bool segmentflaws, bool triflaws)
        {
            Otri horiz = default(Otri);
            Otri top = default(Otri);
            Otri botleft = default(Otri), botright = default(Otri);
            Otri topleft = default(Otri), topright = default(Otri);
            Otri newbotleft = default(Otri), newbotright = default(Otri);
            Otri newtopright = default(Otri);
            Otri botlcasing = default(Otri), botrcasing = default(Otri);
            Otri toplcasing = default(Otri), toprcasing = default(Otri);
            Otri testtri = default(Otri);
            Osub botlsubseg = default(Osub), botrsubseg = default(Osub);
            Osub toplsubseg = default(Osub), toprsubseg = default(Osub);
            Osub brokensubseg = default(Osub);
            Osub checksubseg = default(Osub);
            Osub rightsubseg = default(Osub);
            Osub newsubseg = default(Osub);
            BadSubseg encroached;
            FlipStacker newflip;
            Vertex first;
            Vertex leftvertex, rightvertex, botvertex, topvertex, farvertex;
            Vertex segmentorg, segmentdest;
            double attrib;
            double area;
            InsertVertexResult success;
            LocateResult intersect;
            bool doflip;
            bool mirrorflag;
            bool enq;
            int i;

            if (splitseg.ss == null)
            {
                // Find the location of the vertex to be inserted.  Check if a good
                // starting triangle has already been provided by the caller.
                if (searchtri.triangle == dummytri)
                {
                    // Find a boundary triangle.
                    horiz.triangle = dummytri;
                    horiz.orient = 0;
                    horiz.SymSelf();
                    // Search for a triangle containing 'newvertex'.
                    intersect = Locate(newvertex.pt, ref horiz);
                }
                else
                {
                    // Start searching from the triangle provided by the caller.
                    searchtri.Copy(ref horiz);
                    intersect = PreciseLocate(newvertex.pt, ref horiz, true);
                }
            }
            else
            {
                // The calling routine provides the subsegment in which
                // the vertex is inserted.
                searchtri.Copy(ref horiz);
                intersect = LocateResult.OnEdge;
            }

            if (intersect == LocateResult.OnVertex)
            {
                // There's already a vertex there.  Return in 'searchtri' a triangle
                // whose origin is the existing vertex.
                horiz.Copy(ref searchtri);
                horiz.Copy(ref recenttri);
                return InsertVertexResult.Duplicate;
            }
            if ((intersect == LocateResult.OnEdge) || (intersect == LocateResult.Outside))
            {
                // The vertex falls on an edge or boundary.
                if (checksegments && (splitseg.ss == null))
                {
                    // Check whether the vertex falls on a subsegment.
                    horiz.SegPivot(ref brokensubseg);
                    if (brokensubseg.ss != dummysub)
                    {
                        // The vertex falls on a subsegment, and hence will not be inserted.
                        if (segmentflaws)
                        {
                            enq = Behavior.NoBisect != 2;
                            if (enq && (Behavior.NoBisect == 1))
                            {
                                // This subsegment may be split only if it is an
                                // internal boundary.
                                horiz.Sym(ref testtri);
                                enq = testtri.triangle != dummytri;
                            }
                            if (enq)
                            {
                                // Add the subsegment to the list of encroached subsegments.
                                encroached = new BadSubseg();
                                encroached.encsubseg = brokensubseg;
                                encroached.subsegorg = brokensubseg.Org();
                                encroached.subsegdest = brokensubseg.Dest();

                                quality.AddBadSubseg(encroached);
                            }
                        }
                        // Return a handle whose primary edge contains the vertex,
                        //   which has not been inserted.
                        horiz.Copy(ref searchtri);
                        horiz.Copy(ref recenttri);
                        return InsertVertexResult.Violating;
                    }
                }

                // Insert the vertex on an edge, dividing one triangle into two (if
                // the edge lies on a boundary) or two triangles into four.
                horiz.Lprev(ref botright);
                botright.Sym(ref botrcasing);
                horiz.Sym(ref topright);
                // Is there a second triangle?  (Or does this edge lie on a boundary?)
                mirrorflag = topright.triangle != dummytri;
                if (mirrorflag)
                {
                    topright.LnextSelf();
                    topright.Sym(ref toprcasing);
                    MakeTriangle(ref newtopright);
                }
                else
                {
                    // Splitting a boundary edge increases the number of boundary edges.
                    hullsize++;
                }
                MakeTriangle(ref newbotright);

                // Set the vertices of changed and new triangles.
                rightvertex = horiz.Org();
                leftvertex = horiz.Dest();
                botvertex = horiz.Apex();
                newbotright.SetOrg(botvertex);
                newbotright.SetDest(rightvertex);
                newbotright.SetApex(newvertex);
                horiz.SetOrg(newvertex);

                for (i = 0; i < eextras; i++)
                {
                    // Set the element attributes of a new triangle.
                    newbotright.triangle.attributes[i] = botright.triangle.attributes[i];
                }

                if (Behavior.VarArea)
                {
                    // Set the area constraint of a new triangle.
                    newbotright.triangle.area = botright.triangle.area;
                }

                if (mirrorflag)
                {
                    topvertex = topright.Dest();
                    newtopright.SetOrg(rightvertex);
                    newtopright.SetDest(topvertex);
                    newtopright.SetApex(newvertex);
                    topright.SetOrg(newvertex);

                    for (i = 0; i < eextras; i++)
                    {
                        // Set the element attributes of another new triangle.
                        newtopright.triangle.attributes[i] = topright.triangle.attributes[i];
                    }

                    if (Behavior.VarArea)
                    {
                        // Set the area constraint of another new triangle.
                        newtopright.triangle.area = topright.triangle.area;
                    }
                }

                // There may be subsegments that need to be bonded
                // to the new triangle(s).
                if (checksegments)
                {
                    botright.SegPivot(ref botrsubseg);

                    if (botrsubseg.ss != dummysub)
                    {
                        botright.SegDissolve();
                        newbotright.SegBond(ref botrsubseg);
                    }

                    if (mirrorflag)
                    {
                        topright.SegPivot(ref toprsubseg);
                        if (toprsubseg.ss != dummysub)
                        {
                            topright.SegDissolve();
                            newtopright.SegBond(ref toprsubseg);
                        }
                    }
                }

                // Bond the new triangle(s) to the surrounding triangles.
                newbotright.Bond(ref botrcasing);
                newbotright.LprevSelf();
                newbotright.Bond(ref botright);
                newbotright.LprevSelf();

                if (mirrorflag)
                {
                    newtopright.Bond(ref toprcasing);
                    newtopright.LnextSelf();
                    newtopright.Bond(ref topright);
                    newtopright.LnextSelf();
                    newtopright.Bond(ref newbotright);
                }

                if (splitseg.ss != null)
                {
                    // Split the subsegment into two.
                    splitseg.SetDest(newvertex);
                    segmentorg = splitseg.SegOrg();
                    segmentdest = splitseg.SegDest();
                    splitseg.SymSelf();
                    splitseg.Pivot(ref rightsubseg);
                    InsertSubseg(ref newbotright, splitseg.ss.boundary);
                    newbotright.SegPivot(ref newsubseg);
                    newsubseg.SetSegOrg(segmentorg);
                    newsubseg.SetSegDest(segmentdest);
                    splitseg.Bond(ref newsubseg);
                    newsubseg.SymSelf();
                    newsubseg.Bond(ref rightsubseg);
                    splitseg.SymSelf();

                    // Transfer the subsegment's boundary marker to the vertex if required.
                    if (newvertex.mark == 0)
                    {
                        newvertex.mark = splitseg.ss.boundary;
                    }
                }

                if (checkquality)
                {
                    //poolrestart(&m.flipstackers);  TODO
                    flipstackers.Clear();

                    lastflip = new FlipStacker();
                    lastflip.flippedtri = horiz;
                    lastflip.prevflip = dummyflip;

                    flipstackers.Add(lastflip); // TODO: Could be ok???

                    //throw new Exception("insertvertex: what to do here??");
                }

                // Position 'horiz' on the first edge to check for
                // the Delaunay property.
                horiz.LnextSelf();
            }
            else
            {
                // Insert the vertex in a triangle, splitting it into three.
                horiz.Lnext(ref botleft);
                horiz.Lprev(ref botright);
                botleft.Sym(ref botlcasing);
                botright.Sym(ref botrcasing);
                MakeTriangle(ref newbotleft);
                MakeTriangle(ref newbotright);

                // Set the vertices of changed and new triangles.
                rightvertex = horiz.Org();
                leftvertex = horiz.Dest();
                botvertex = horiz.Apex();
                newbotleft.SetOrg(leftvertex);
                newbotleft.SetDest(botvertex);
                newbotleft.SetApex(newvertex);
                newbotright.SetOrg(botvertex);
                newbotright.SetDest(rightvertex);
                newbotright.SetApex(newvertex);
                horiz.SetApex(newvertex);

                for (i = 0; i < eextras; i++)
                {
                    // Set the element attributes of the new triangles.
                    attrib = horiz.triangle.attributes[i];
                    newbotleft.triangle.attributes[i] = attrib;
                    newbotright.triangle.attributes[i] = attrib;
                }

                if (Behavior.VarArea)
                {
                    // Set the area constraint of the new triangles.
                    area = horiz.triangle.area;
                    newbotleft.triangle.area = area;
                    newbotright.triangle.area = area;
                }

                // There may be subsegments that need to be bonded
                //   to the new triangles.
                if (checksegments)
                {
                    botleft.SegPivot(ref botlsubseg);
                    if (botlsubseg.ss != dummysub)
                    {
                        botleft.SegDissolve();
                        newbotleft.SegBond(ref botlsubseg);
                    }
                    botright.SegPivot(ref botrsubseg);
                    if (botrsubseg.ss != dummysub)
                    {
                        botright.SegDissolve();
                        newbotright.SegBond(ref botrsubseg);
                    }
                }

                // Bond the new triangles to the surrounding triangles.
                newbotleft.Bond(ref botlcasing);
                newbotright.Bond(ref botrcasing);
                newbotleft.LnextSelf();
                newbotright.LprevSelf();
                newbotleft.Bond(ref newbotright);
                newbotleft.LnextSelf();
                botleft.Bond(ref newbotleft);
                newbotright.LprevSelf();
                botright.Bond(ref newbotright);

                if (checkquality)
                {
                    // poolrestart(&m->flipstackers); TODO
                    flipstackers.Clear();

                    lastflip = new FlipStacker();
                    lastflip.flippedtri = horiz;
                    lastflip.prevflip = null;

                    flipstackers.Add(lastflip);
                }
            }

            // The insertion is successful by default, unless an encroached
            // subsegment is found.
            success = InsertVertexResult.Successful;
            // Circle around the newly inserted vertex, checking each edge opposite
            // it for the Delaunay property.  Non-Delaunay edges are flipped.
            // 'horiz' is always the edge being checked.  'first' marks where to
            // stop circling.
            first = horiz.Org();
            rightvertex = first;
            leftvertex = horiz.Dest();
            // Circle until finished.
            while (true)
            {
                // By default, the edge will be flipped.
                doflip = true;

                if (checksegments)
                {
                    // Check for a subsegment, which cannot be flipped.
                    horiz.SegPivot(ref checksubseg);
                    if (checksubseg.ss != dummysub)
                    {
                        // The edge is a subsegment and cannot be flipped.
                        doflip = false;

                        if (segmentflaws)
                        {
                            // Does the new vertex encroach upon this subsegment?
                            if (quality.CheckSeg4Encroach(ref checksubseg) > 0)
                            {
                                success = InsertVertexResult.Encroaching;
                            }
                        }
                    }
                }

                if (doflip)
                {
                    // Check if the edge is a boundary edge.
                    horiz.Sym(ref top);
                    if (top.triangle == dummytri)
                    {
                        // The edge is a boundary edge and cannot be flipped.
                        doflip = false;
                    }
                    else
                    {
                        // Find the vertex on the other side of the edge.
                        farvertex = top.Apex();
                        // In the incremental Delaunay triangulation algorithm, any of
                        // 'leftvertex', 'rightvertex', and 'farvertex' could be vertices
                        // of the triangular bounding box.  These vertices must be
                        // treated as if they are infinitely distant, even though their
                        // "coordinates" are not.
                        if ((leftvertex == infvertex1) || (leftvertex == infvertex2) ||
                            (leftvertex == infvertex3))
                        {
                            // 'leftvertex' is infinitely distant.  Check the convexity of
                            // the boundary of the triangulation.  'farvertex' might be
                            // infinite as well, but trust me, this same condition should
                            // be applied.
                            doflip = Primitives.CounterClockwise(newvertex.pt, rightvertex.pt, farvertex.pt) > 0.0;
                        }
                        else if ((rightvertex == infvertex1) ||
                                 (rightvertex == infvertex2) ||
                                 (rightvertex == infvertex3))
                        {
                            // 'rightvertex' is infinitely distant. Check the convexity of
                            // the boundary of the triangulation. 'farvertex' might be
                            // infinite as well, but trust me, this same condition should
                            // be applied.
                            doflip = Primitives.CounterClockwise(farvertex.pt, leftvertex.pt, newvertex.pt) > 0.0;
                        }
                        else if ((farvertex == infvertex1) ||
                                 (farvertex == infvertex2) ||
                                 (farvertex == infvertex3))
                        {
                            // 'farvertex' is infinitely distant and cannot be inside
                            // the circumcircle of the triangle 'horiz'.
                            doflip = false;
                        }
                        else
                        {
                            // Test whether the edge is locally Delaunay.
                            doflip = Primitives.InCircle(leftvertex.pt, newvertex.pt, rightvertex.pt, farvertex.pt) > 0.0;
                        }
                        if (doflip)
                        {
                            // We made it! Flip the edge 'horiz' by rotating its containing
                            // quadrilateral (the two triangles adjacent to 'horiz').
                            // Identify the casing of the quadrilateral.
                            top.Lprev(ref topleft);
                            topleft.Sym(ref toplcasing);
                            top.Lnext(ref topright);
                            topright.Sym(ref toprcasing);
                            horiz.Lnext(ref botleft);
                            botleft.Sym(ref botlcasing);
                            horiz.Lprev(ref botright);
                            botright.Sym(ref botrcasing);
                            // Rotate the quadrilateral one-quarter turn counterclockwise.
                            topleft.Bond(ref botlcasing);
                            botleft.Bond(ref botrcasing);
                            botright.Bond(ref toprcasing);
                            topright.Bond(ref toplcasing);
                            if (checksegments)
                            {
                                // Check for subsegments and rebond them to the quadrilateral.
                                topleft.SegPivot(ref toplsubseg);
                                botleft.SegPivot(ref botlsubseg);
                                botright.SegPivot(ref botrsubseg);
                                topright.SegPivot(ref toprsubseg);
                                if (toplsubseg.ss == dummysub)
                                {
                                    topright.SegDissolve();
                                }
                                else
                                {
                                    topright.SegBond(ref toplsubseg);
                                }
                                if (botlsubseg.ss == dummysub)
                                {
                                    topleft.SegDissolve();
                                }
                                else
                                {
                                    topleft.SegBond(ref botlsubseg);
                                }
                                if (botrsubseg.ss == dummysub)
                                {
                                    botleft.SegDissolve();
                                }
                                else
                                {
                                    botleft.SegBond(ref botrsubseg);
                                }
                                if (toprsubseg.ss == dummysub)
                                {
                                    botright.SegDissolve();
                                }
                                else
                                {
                                    botright.SegBond(ref toprsubseg);
                                }
                            }
                            // New vertex assignments for the rotated quadrilateral.
                            horiz.SetOrg(farvertex);
                            horiz.SetDest(newvertex);
                            horiz.SetApex(rightvertex);
                            top.SetOrg(newvertex);
                            top.SetDest(farvertex);
                            top.SetApex(leftvertex);

                            for (i = 0; i < eextras; i++)
                            {
                                // Take the average of the two triangles' attributes.
                                attrib = 0.5 * (top.triangle.attributes[i] + horiz.triangle.attributes[i]);
                                top.triangle.attributes[i] = attrib;
                                horiz.triangle.attributes[i] = attrib;
                            }

                            if (Behavior.VarArea)
                            {
                                if ((top.triangle.area <= 0.0) || (horiz.triangle.area <= 0.0))
                                {
                                    area = -1.0;
                                }
                                else
                                {
                                    // Take the average of the two triangles' area constraints.
                                    // This prevents small area constraints from migrating a
                                    // long, long way from their original location due to flips.
                                    area = 0.5 * (top.triangle.area + horiz.triangle.area);
                                }

                                top.triangle.area = area;
                                horiz.triangle.area = area;
                            }

                            if (checkquality)
                            {
                                newflip = new FlipStacker();
                                newflip.flippedtri = horiz;
                                newflip.prevflip = lastflip;
                                lastflip = newflip;

                                flipstackers.Add(newflip);
                            }

                            // On the next iterations, consider the two edges that were
                            // exposed (this is, are now visible to the newly inserted
                            // vertex) by the edge flip.
                            horiz.LprevSelf();
                            leftvertex = farvertex;
                        }
                    }
                }
                if (!doflip)
                {
                    // The handle 'horiz' is accepted as locally Delaunay.
                    if (triflaws)
                    {
                        // Check the triangle 'horiz' for quality.
                        quality.TestTriangle(ref horiz);
                    }

                    // Look for the next edge around the newly inserted vertex.
                    horiz.LnextSelf();
                    horiz.Sym(ref testtri);
                    // Check for finishing a complete revolution about the new vertex, or
                    // falling outside  of the triangulation.  The latter will happen
                    // when a vertex is inserted at a boundary.
                    if ((leftvertex == first) || (testtri.triangle == dummytri))
                    {
                        // We're done.  Return a triangle whose origin is the new vertex.
                        horiz.Lnext(ref searchtri);
                        horiz.Lnext(ref recenttri);
                        return success;
                    }
                    // Finish finding the next edge around the newly inserted vertex.
                    testtri.Lnext(ref horiz);
                    rightvertex = leftvertex;
                    leftvertex = horiz.Dest();
                }
            }
        }

        /// <summary>
        /// Create a new subsegment and inserts it between two triangles. Its 
        /// vertices are properly initialized.
        /// </summary>
        /// <param name="tri">The new subsegment is inserted at the edge 
        /// described by this handle.</param>
        /// <param name="subsegmark">The marker 'subsegmark' is applied to the 
        /// subsegment and, if appropriate, its vertices.</param>
        internal void InsertSubseg(ref Otri tri, int subsegmark)
        {
            Otri oppotri = default(Otri);
            Osub newsubseg = default(Osub);
            Vertex triorg, tridest;

            triorg = tri.Org();
            tridest = tri.Dest();
            // Mark vertices if possible.
            if (triorg.mark == 0)
            {
                triorg.mark = subsegmark;
            }
            if (tridest.mark == 0)
            {
                tridest.mark = subsegmark;
            }
            // Check if there's already a subsegment here.
            tri.SegPivot(ref newsubseg);
            if (newsubseg.ss == dummysub)
            {
                // Make new subsegment and initialize its vertices.
                MakeSubseg(ref newsubseg);
                newsubseg.SetOrg(tridest);
                newsubseg.SetDest(triorg);
                newsubseg.SetSegOrg(tridest);
                newsubseg.SetSegDest(triorg);
                // Bond new subsegment to the two triangles it is sandwiched between.
                // Note that the facing triangle 'oppotri' might be equal to
                // 'dummytri' (outer space), but the new subsegment is bonded to it
                // all the same.
                tri.SegBond(ref newsubseg);
                tri.Sym(ref oppotri);
                newsubseg.SymSelf();
                oppotri.SegBond(ref newsubseg);
                newsubseg.ss.boundary = subsegmark;
            }
            else
            {
                if (newsubseg.ss.boundary == 0)
                {
                    newsubseg.ss.boundary = subsegmark;
                }
            }
        }

        /// <summary>
        /// Transform two triangles to two different triangles by flipping an edge 
        /// counterclockwise within a quadrilateral.
        /// </summary>
        /// <param name="flipedge"></param>
        /// <remarks>Imagine the original triangles, abc and bad, oriented so that the
        /// shared edge ab lies in a horizontal plane, with the vertex b on the left
        /// and the vertex a on the right.  The vertex c lies below the edge, and
        /// the vertex d lies above the edge.  The 'flipedge' handle holds the edge
        /// ab of triangle abc, and is directed left, from vertex a to vertex b.
        ///
        /// The triangles abc and bad are deleted and replaced by the triangles cdb
        /// and dca.  The triangles that represent abc and bad are NOT deallocated;
        /// they are reused for dca and cdb, respectively.  Hence, any handles that
        /// may have held the original triangles are still valid, although not
        /// directed as they were before.
        ///
        /// Upon completion of this routine, the 'flipedge' handle holds the edge
        /// dc of triangle dca, and is directed down, from vertex d to vertex c.
        /// (Hence, the two triangles have rotated counterclockwise.)
        ///
        /// WARNING:  This transformation is geometrically valid only if the
        /// quadrilateral adbc is convex.  Furthermore, this transformation is
        /// valid only if there is not a subsegment between the triangles abc and
        /// bad.  This routine does not check either of these preconditions, and
        /// it is the responsibility of the calling routine to ensure that they are
        /// met.  If they are not, the streets shall be filled with wailing and
        /// gnashing of teeth.
        /// 
        /// Terminology
        ///
        /// A "local transformation" replaces a small set of triangles with another
        /// set of triangles.  This may or may not involve inserting or deleting a
        /// vertex.
        ///
        /// The term "casing" is used to describe the set of triangles that are
        /// attached to the triangles being transformed, but are not transformed
        /// themselves.  Think of the casing as a fixed hollow structure inside
        /// which all the action happens.  A "casing" is only defined relative to
        /// a single transformation; each occurrence of a transformation will
        /// involve a different casing.
        /// </remarks>
        internal void Flip(ref Otri flipedge)
        {
            Otri botleft = default(Otri), botright = default(Otri);
            Otri topleft = default(Otri), topright = default(Otri);
            Otri top = default(Otri);
            Otri botlcasing = default(Otri), botrcasing = default(Otri);
            Otri toplcasing = default(Otri), toprcasing = default(Otri);
            Osub botlsubseg = default(Osub), botrsubseg = default(Osub);
            Osub toplsubseg = default(Osub), toprsubseg = default(Osub);
            Vertex leftvertex, rightvertex, botvertex;
            Vertex farvertex;

            // Identify the vertices of the quadrilateral.
            rightvertex = flipedge.Org();
            leftvertex = flipedge.Dest();
            botvertex = flipedge.Apex();
            flipedge.Sym(ref top);

            farvertex = top.Apex();

            // Identify the casing of the quadrilateral.
            top.Lprev(ref topleft);
            topleft.Sym(ref toplcasing);
            top.Lnext(ref topright);
            topright.Sym(ref toprcasing);
            flipedge.Lnext(ref botleft);
            botleft.Sym(ref botlcasing);
            flipedge.Lprev(ref botright);
            botright.Sym(ref botrcasing);
            // Rotate the quadrilateral one-quarter turn counterclockwise.
            topleft.Bond(ref botlcasing);
            botleft.Bond(ref botrcasing);
            botright.Bond(ref toprcasing);
            topright.Bond(ref toplcasing);

            if (checksegments)
            {
                // Check for subsegments and rebond them to the quadrilateral.
                topleft.SegPivot(ref toplsubseg);
                botleft.SegPivot(ref botlsubseg);
                botright.SegPivot(ref botrsubseg);
                topright.SegPivot(ref toprsubseg);
                if (toplsubseg.ss == Mesh.dummysub)
                {
                    topright.SegDissolve();
                }
                else
                {
                    topright.SegBond(ref toplsubseg);
                }
                if (botlsubseg.ss == Mesh.dummysub)
                {
                    topleft.SegDissolve();
                }
                else
                {
                    topleft.SegBond(ref botlsubseg);
                }
                if (botrsubseg.ss == Mesh.dummysub)
                {
                    botleft.SegDissolve();
                }
                else
                {
                    botleft.SegBond(ref botrsubseg);
                }
                if (toprsubseg.ss == Mesh.dummysub)
                {
                    botright.SegDissolve();
                }
                else
                {
                    botright.SegBond(ref toprsubseg);
                }
            }

            // New vertex assignments for the rotated quadrilateral.
            flipedge.SetOrg(farvertex);
            flipedge.SetDest(botvertex);
            flipedge.SetApex(rightvertex);
            top.SetOrg(botvertex);
            top.SetDest(farvertex);
            top.SetApex(leftvertex);
        }

        /// <summary>
        /// Transform two triangles to two different triangles by flipping an edge 
        /// clockwise within a quadrilateral.  Reverses the flip() operation so that 
        /// the data structures representing the triangles are back where they were 
        /// before the flip().
        /// </summary>
        /// <param name="flipedge"></param>
        /// <remarks>
        /// Imagine the original triangles, abc and bad, oriented so that the
        /// shared edge ab lies in a horizontal plane, with the vertex b on the left
        /// and the vertex a on the right.  The vertex c lies below the edge, and
        /// the vertex d lies above the edge.  The 'flipedge' handle holds the edge
        /// ab of triangle abc, and is directed left, from vertex a to vertex b.
        ///
        /// The triangles abc and bad are deleted and replaced by the triangles cdb
        /// and dca.  The triangles that represent abc and bad are NOT deallocated;
        /// they are reused for cdb and dca, respectively.  Hence, any handles that
        /// may have held the original triangles are still valid, although not
        /// directed as they were before.
        ///
        /// Upon completion of this routine, the 'flipedge' handle holds the edge
        /// cd of triangle cdb, and is directed up, from vertex c to vertex d.
        /// (Hence, the two triangles have rotated clockwise.)
        ///
        /// WARNING:  This transformation is geometrically valid only if the
        /// quadrilateral adbc is convex.  Furthermore, this transformation is
        /// valid only if there is not a subsegment between the triangles abc and
        /// bad.  This routine does not check either of these preconditions, and
        /// it is the responsibility of the calling routine to ensure that they are
        /// met.  If they are not, the streets shall be filled with wailing and
        /// gnashing of teeth.
        /// </remarks>
        internal void Unflip(ref Otri flipedge)
        {
            Otri botleft = default(Otri), botright = default(Otri);
            Otri topleft = default(Otri), topright = default(Otri);
            Otri top = default(Otri);
            Otri botlcasing = default(Otri), botrcasing = default(Otri);
            Otri toplcasing = default(Otri), toprcasing = default(Otri);
            Osub botlsubseg = default(Osub), botrsubseg = default(Osub);
            Osub toplsubseg = default(Osub), toprsubseg = default(Osub);
            Vertex leftvertex, rightvertex, botvertex;
            Vertex farvertex;

            // Identify the vertices of the quadrilateral.
            rightvertex = flipedge.Org();
            leftvertex = flipedge.Dest();
            botvertex = flipedge.Apex();
            flipedge.Sym(ref top);

            farvertex = top.Apex();

            // Identify the casing of the quadrilateral.
            top.Lprev(ref topleft);
            topleft.Sym(ref toplcasing);
            top.Lnext(ref topright);
            topright.Sym(ref toprcasing);
            flipedge.Lnext(ref botleft);
            botleft.Sym(ref botlcasing);
            flipedge.Lprev(ref botright);
            botright.Sym(ref botrcasing);
            // Rotate the quadrilateral one-quarter turn clockwise.
            topleft.Bond(ref toprcasing);
            botleft.Bond(ref toplcasing);
            botright.Bond(ref botlcasing);
            topright.Bond(ref botrcasing);

            if (checksegments)
            {
                // Check for subsegments and rebond them to the quadrilateral.
                topleft.SegPivot(ref toplsubseg);
                botleft.SegPivot(ref botlsubseg);
                botright.SegPivot(ref botrsubseg);
                topright.SegPivot(ref toprsubseg);
                if (toplsubseg.ss == Mesh.dummysub)
                {
                    botleft.SegDissolve();
                }
                else
                {
                    botleft.SegBond(ref toplsubseg);
                }
                if (botlsubseg.ss == Mesh.dummysub)
                {
                    botright.SegDissolve();
                }
                else
                {
                    botright.SegBond(ref botlsubseg);
                }
                if (botrsubseg.ss == Mesh.dummysub)
                {
                    topright.SegDissolve();
                }
                else
                {
                    topright.SegBond(ref botrsubseg);
                }
                if (toprsubseg.ss == Mesh.dummysub)
                {
                    topleft.SegDissolve();
                }
                else
                {
                    topleft.SegBond(ref toprsubseg);
                }
            }

            // New vertex assignments for the rotated quadrilateral.
            flipedge.SetOrg(botvertex);
            flipedge.SetDest(farvertex);
            flipedge.SetApex(leftvertex);
            top.SetOrg(farvertex);
            top.SetDest(botvertex);
            top.SetApex(rightvertex);
        }

        /// <summary>
        /// Find the Delaunay triangulation of a polygon that has a certain "nice" shape. 
        /// This includes the polygons that result from deletion of a vertex or insertion 
        /// of a segment.
        /// </summary>
        /// <param name="firstedge">The primary edge of the first triangle.</param>
        /// <param name="lastedge">The primary edge of the last triangle.</param>
        /// <param name="edgecount">The number of sides of the polygon, including its 
        /// base.</param>
        /// <param name="doflip">A flag, wether to perform the last flip.</param>
        /// <param name="triflaws">A flag that determines whether the new triangles should 
        /// be tested for quality, and enqueued if they are bad.</param>
        /// <remarks>
        //  This is a conceptually difficult routine.  The starting assumption is
        //  that we have a polygon with n sides.  n - 1 of these sides are currently
        //  represented as edges in the mesh.  One side, called the "base", need not
        //  be.
        //
        //  Inside the polygon is a structure I call a "fan", consisting of n - 1
        //  triangles that share a common origin. For each of these triangles, the
        //  edge opposite the origin is one of the sides of the polygon.  The
        //  primary edge of each triangle is the edge directed from the origin to
        //  the destination; note that this is not the same edge that is a side of
        //  the polygon. 'firstedge' is the primary edge of the first triangle.
        //  From there, the triangles follow in counterclockwise order about the
        //  polygon, until 'lastedge', the primary edge of the last triangle.
        //  'firstedge' and 'lastedge' are probably connected to other triangles
        //  beyond the extremes of the fan, but their identity is not important, as
        //  long as the fan remains connected to them.
        //
        //  Imagine the polygon oriented so that its base is at the bottom.  This
        //  puts 'firstedge' on the far right, and 'lastedge' on the far left.
        //  The right vertex of the base is the destination of 'firstedge', and the
        //  left vertex of the base is the apex of 'lastedge'.
        //
        //  The challenge now is to find the right sequence of edge flips to
        //  transform the fan into a Delaunay triangulation of the polygon.  Each
        //  edge flip effectively removes one triangle from the fan, committing it
        //  to the polygon.  The resulting polygon has one fewer edge. If 'doflip'
        //  is set, the final flip will be performed, resulting in a fan of one
        //  (useless?) triangle. If 'doflip' is not set, the final flip is not
        //  performed, resulting in a fan of two triangles, and an unfinished
        //  triangular polygon that is not yet filled out with a single triangle.
        //  On completion of the routine, 'lastedge' is the last remaining triangle,
        //  or the leftmost of the last two.
        //
        //  Although the flips are performed in the order described above, the
        //  decisions about what flips to perform are made in precisely the reverse
        //  order. The recursive triangulatepolygon() procedure makes a decision,
        //  uses up to two recursive calls to triangulate the "subproblems"
        //  (polygons with fewer edges), and then performs an edge flip.
        //
        //  The "decision" it makes is which vertex of the polygon should be
        //  connected to the base. This decision is made by testing every possible
        //  vertex.  Once the best vertex is found, the two edges that connect this
        //  vertex to the base become the bases for two smaller polygons. These
        //  are triangulated recursively. Unfortunately, this approach can take
        //  O(n^2) time not only in the worst case, but in many common cases. It's
        //  rarely a big deal for vertex deletion, where n is rarely larger than
        //  ten, but it could be a big deal for segment insertion, especially if
        //  there's a lot of long segments that each cut many triangles. I ought to
        //  code a faster algorithm some day.
        /// </remarks>
        private void TriangulatePolygon(Otri firstedge, Otri lastedge,
                                int edgecount, bool doflip, bool triflaws)
        {
            Otri testtri = default(Otri);
            Otri besttri = default(Otri);
            Otri tempedge = default(Otri);
            Vertex leftbasevertex, rightbasevertex;
            Vertex testvertex;
            Vertex bestvertex;
            int bestnumber;
            int i;

            // Identify the base vertices.
            leftbasevertex = lastedge.Apex();
            rightbasevertex = firstedge.Dest();

            // Find the best vertex to connect the base to.
            firstedge.Onext(ref besttri);
            bestvertex = besttri.Dest();
            besttri.Copy(ref testtri);
            bestnumber = 1;
            for (i = 2; i <= edgecount - 2; i++)
            {
                testtri.OnextSelf();
                testvertex = testtri.Dest();
                // Is this a better vertex?
                if (Primitives.InCircle(leftbasevertex.pt, rightbasevertex.pt, bestvertex.pt, testvertex.pt) > 0.0)
                {
                    testtri.Copy(ref besttri);
                    bestvertex = testvertex;
                    bestnumber = i;
                }
            }

            if (bestnumber > 1)
            {
                // Recursively triangulate the smaller polygon on the right.
                besttri.Oprev(ref tempedge);
                TriangulatePolygon(firstedge, tempedge, bestnumber + 1, true, triflaws);
            }

            if (bestnumber < edgecount - 2)
            {
                // Recursively triangulate the smaller polygon on the left.
                besttri.Sym(ref tempedge);
                TriangulatePolygon(besttri, lastedge, edgecount - bestnumber, true, triflaws);
                // Find 'besttri' again; it may have been lost to edge flips.
                tempedge.Sym(ref besttri);
            }

            if (doflip)
            {
                // Do one final edge flip.
                Flip(ref besttri);
                if (triflaws)
                {
                    // Check the quality of the newly committed triangle.
                    besttri.Sym(ref testtri);
                    quality.TestTriangle(ref testtri);
                }
            }
            // Return the base triangle.
            besttri.Copy(ref lastedge);
        }

        /// <summary>
        /// Delete a vertex from a Delaunay triangulation, ensuring that the 
        /// triangulation remains Delaunay.
        /// </summary>
        /// <param name="deltri"></param>
        /// <remarks>The origin of 'deltri' is deleted. The union of the triangles adjacent
        /// to this vertex is a polygon, for which the Delaunay triangulation is
        /// found. Two triangles are removed from the mesh.
        ///
        /// Only interior vertices that do not lie on segments or boundaries may be
        /// deleted.
        /// </remarks>
        internal void DeleteVertex(ref Otri deltri)
        {
            Otri countingtri = default(Otri);
            Otri firstedge = default(Otri), lastedge = default(Otri);
            Otri deltriright = default(Otri);
            Otri lefttri = default(Otri), righttri = default(Otri);
            Otri leftcasing = default(Otri), rightcasing = default(Otri);
            Osub leftsubseg = default(Osub), rightsubseg = default(Osub);
            Vertex delvertex;
            Vertex neworg;
            int edgecount;

            delvertex = deltri.Org();

            VertexDealloc(delvertex);

            // Count the degree of the vertex being deleted.
            deltri.Onext(ref countingtri);
            edgecount = 1;
            while (!deltri.Equal(countingtri))
            {
                edgecount++;
                countingtri.OnextSelf();
            }

            if (edgecount > 3)
            {
                // Triangulate the polygon defined by the union of all triangles
                // adjacent to the vertex being deleted.  Check the quality of
                // the resulting triangles.
                deltri.Onext(ref firstedge);
                deltri.Oprev(ref lastedge);
                TriangulatePolygon(firstedge, lastedge, edgecount, false, Behavior.NoBisect == 0);
            }
            // Splice out two triangles.
            deltri.Lprev(ref deltriright);
            deltri.Dnext(ref lefttri);
            lefttri.Sym(ref leftcasing);
            deltriright.Oprev(ref righttri);
            righttri.Sym(ref rightcasing);
            deltri.Bond(ref leftcasing);
            deltriright.Bond(ref rightcasing);
            lefttri.SegPivot(ref leftsubseg);
            if (leftsubseg.ss != Mesh.dummysub)
            {
                deltri.SegBond(ref leftsubseg);
            }
            righttri.SegPivot(ref rightsubseg);
            if (rightsubseg.ss != Mesh.dummysub)
            {
                deltriright.SegBond(ref rightsubseg);
            }

            // Set the new origin of 'deltri' and check its quality.
            neworg = lefttri.Org();
            deltri.SetOrg(neworg);
            if (Behavior.NoBisect == 0)
            {
                quality.TestTriangle(ref deltri);
            }

            // Delete the two spliced-out triangles.
            TriangleDealloc(lefttri.triangle);
            TriangleDealloc(righttri.triangle);
        }

        /// <summary>
        /// Undo the most recent vertex insertion.
        /// </summary>
        /// <remarks>
        /// Walks through the list of transformations (flips and a vertex insertion)
        /// in the reverse of the order in which they were done, and undoes them.
        /// The inserted vertex is removed from the triangulation and deallocated.
        /// Two triangles (possibly just one) are also deallocated.
        /// </remarks>
        internal void UndoVertex()
        {
            Otri fliptri = default(Otri);
            Otri botleft = default(Otri), botright = default(Otri), topright = default(Otri);
            Otri botlcasing = default(Otri), botrcasing = default(Otri), toprcasing = default(Otri);
            Otri gluetri = default(Otri);
            Osub botlsubseg = default(Osub), botrsubseg = default(Osub), toprsubseg = default(Osub);
            Vertex botvertex, rightvertex;

            // Walk through the list of transformations (flips and a vertex insertion)
            // in the reverse of the order in which they were done, and undo them.
            while (lastflip != null)
            {
                // Find a triangle involved in the last unreversed transformation.
                fliptri = lastflip.flippedtri;

                // We are reversing one of three transformations:  a trisection of one
                // triangle into three (by inserting a vertex in the triangle), a
                // bisection of two triangles into four (by inserting a vertex in an
                // edge), or an edge flip.
                if (lastflip.prevflip == null)
                {
                    // Restore a triangle that was split into three triangles,
                    // so it is again one triangle.
                    fliptri.Dprev(ref botleft);
                    botleft.LnextSelf();
                    fliptri.Onext(ref botright);
                    botright.LprevSelf();
                    botleft.Sym(ref botlcasing);
                    botright.Sym(ref botrcasing);
                    botvertex = botleft.Dest();

                    fliptri.SetApex(botvertex);
                    fliptri.LnextSelf();
                    fliptri.Bond(ref botlcasing);
                    botleft.SegPivot(ref botlsubseg);
                    fliptri.SegBond(ref botlsubseg);
                    fliptri.LnextSelf();
                    fliptri.Bond(ref botrcasing);
                    botright.SegPivot(ref botrsubseg);
                    fliptri.SegBond(ref botrsubseg);

                    // Delete the two spliced-out triangles.
                    TriangleDealloc(botleft.triangle);
                    TriangleDealloc(botright.triangle);
                }
                else if (lastflip.prevflip == dummyflip)
                {
                    // Restore two triangles that were split into four triangles,
                    // so they are again two triangles.
                    fliptri.Lprev(ref gluetri);
                    gluetri.Sym(ref botright);
                    botright.LnextSelf();
                    botright.Sym(ref botrcasing);
                    rightvertex = botright.Dest();

                    fliptri.SetOrg(rightvertex);
                    gluetri.Bond(ref botrcasing);
                    botright.SegPivot(ref botrsubseg);
                    gluetri.SegBond(ref botrsubseg);

                    // Delete the spliced-out triangle.
                    TriangleDealloc(botright.triangle);

                    fliptri.Sym(ref gluetri);
                    if (gluetri.triangle != Mesh.dummytri)
                    {
                        gluetri.LnextSelf();
                        gluetri.Dnext(ref topright);
                        topright.Sym(ref toprcasing);

                        gluetri.SetOrg(rightvertex);
                        gluetri.Bond(ref toprcasing);
                        topright.SegPivot(ref toprsubseg);
                        gluetri.SegBond(ref toprsubseg);

                        // Delete the spliced-out triangle.
                        TriangleDealloc(topright.triangle);
                    }

                    // This is the end of the list, sneakily encoded.
                    lastflip.prevflip = null;
                }
                else
                {
                    // Undo an edge flip.
                    Unflip(ref fliptri);
                }

                // Go on and process the next transformation.
                lastflip = lastflip.prevflip;
            }
        }

        #endregion

        #region Location

        /// <summary>
        /// Construct a mapping from vertices to triangles to improve the speed of 
        /// point location for segment insertion.
        /// </summary>
        /// <remarks>
        /// Traverses all the triangles, and provides each corner of each triangle
        /// with a pointer to that triangle.  Of course, pointers will be
        /// overwritten by other pointers because (almost) each vertex is a corner
        /// of several triangles, but in the end every vertex will point to some
        /// triangle that contains it.
        /// </remarks>
        void MakeVertexMap()
        {
            Otri tri = default(Otri);
            Vertex triorg;

            foreach (var t in this.triangles.Values)
            {
                tri.triangle = t;
                // Check all three vertices of the triangle.
                for (tri.orient = 0; tri.orient < 3; tri.orient++)
                {
                    triorg = tri.Org();
                    triorg.tri = tri;
                }
            }
        }

        /// <summary>
        /// Find a triangle or edge containing a given point.
        /// </summary>
        /// <param name="searchpoint">The point to locate.</param>
        /// <param name="searchtri">The triangle to start the search at.</param>
        /// <param name="stopatsubsegment"> If 'stopatsubsegment' is set, the search 
        /// will stop if it tries to walk through a subsegment, and will return OUTSIDE.</param>
        /// <returns>Location information.</returns>
        /// <remarks>
        /// Begins its search from 'searchtri'.  It is important that 'searchtri'
        /// be a handle with the property that 'searchpoint' is strictly to the left
        /// of the edge denoted by 'searchtri', or is collinear with that edge and
        /// does not intersect that edge.  (In particular, 'searchpoint' should not
        /// be the origin or destination of that edge.)
        ///
        /// These conditions are imposed because preciselocate() is normally used in
        /// one of two situations:
        ///
        /// (1)  To try to find the location to insert a new point.  Normally, we
        ///      know an edge that the point is strictly to the left of.  In the
        ///      incremental Delaunay algorithm, that edge is a bounding box edge.
        ///      In Ruppert's Delaunay refinement algorithm for quality meshing,
        ///      that edge is the shortest edge of the triangle whose circumcenter
        ///      is being inserted.
        ///
        /// (2)  To try to find an existing point.  In this case, any edge on the
        ///      convex hull is a good starting edge.  You must screen out the
        ///      possibility that the vertex sought is an endpoint of the starting
        ///      edge before you call preciselocate().
        ///
        /// On completion, 'searchtri' is a triangle that contains 'searchpoint'.
        ///
        /// This implementation differs from that given by Guibas and Stolfi.  It
        /// walks from triangle to triangle, crossing an edge only if 'searchpoint'
        /// is on the other side of the line containing that edge.  After entering
        /// a triangle, there are two edges by which one can leave that triangle.
        /// If both edges are valid ('searchpoint' is on the other side of both
        /// edges), one of the two is chosen by drawing a line perpendicular to
        /// the entry edge (whose endpoints are 'forg' and 'fdest') passing through
        /// 'fapex'.  Depending on which side of this perpendicular 'searchpoint'
        /// falls on, an exit edge is chosen.
        ///
        /// This implementation is empirically faster than the Guibas and Stolfi
        /// point location routine (which I originally used), which tends to spiral
        /// in toward its target.
        ///
        /// Returns ONVERTEX if the point lies on an existing vertex.  'searchtri'
        /// is a handle whose origin is the existing vertex.
        ///
        /// Returns ONEDGE if the point lies on a mesh edge.  'searchtri' is a
        /// handle whose primary edge is the edge on which the point lies.
        ///
        /// Returns INTRIANGLE if the point lies strictly within a triangle.
        /// 'searchtri' is a handle on the triangle that contains the point.
        ///
        /// Returns OUTSIDE if the point lies outside the mesh.  'searchtri' is a
        /// handle whose primary edge the point is to the right of.  This might
        /// occur when the circumcenter of a triangle falls just slightly outside
        /// the mesh due to floating-point roundoff error.  It also occurs when
        /// seeking a hole or region point that a foolish user has placed outside
        /// the mesh.
        ///
        /// WARNING:  This routine is designed for convex triangulations, and will
        /// not generally work after the holes and concavities have been carved.
        /// However, it can still be used to find the circumcenter of a triangle, as
        /// long as the search is begun from the triangle in question.</remarks>
        internal LocateResult PreciseLocate(Point2 searchpoint, ref Otri searchtri,
                                        bool stopatsubsegment)
        {
            Otri backtracktri = default(Otri);
            Osub checkedge = default(Osub);
            Vertex forg, fdest, fapex;
            double orgorient, destorient;
            bool moveleft;

            // Where are we?
            forg = searchtri.Org();
            fdest = searchtri.Dest();
            fapex = searchtri.Apex();
            while (true)
            {
                // Check whether the apex is the point we seek.
                if ((fapex.pt.X == searchpoint.X) && (fapex.pt.Y == searchpoint.Y))
                {
                    searchtri.LprevSelf();
                    return LocateResult.OnVertex;
                }
                // Does the point lie on the other side of the line defined by the
                // triangle edge opposite the triangle's destination?
                destorient = Primitives.CounterClockwise(forg.pt, fapex.pt, searchpoint);
                // Does the point lie on the other side of the line defined by the
                // triangle edge opposite the triangle's origin?
                orgorient = Primitives.CounterClockwise(fapex.pt, fdest.pt, searchpoint);
                if (destorient > 0.0)
                {
                    if (orgorient > 0.0)
                    {
                        // Move left if the inner product of (fapex - searchpoint) and
                        // (fdest - forg) is positive.  This is equivalent to drawing
                        // a line perpendicular to the line (forg, fdest) and passing
                        // through 'fapex', and determining which side of this line
                        // 'searchpoint' falls on.
                        moveleft = (fapex.pt.X - searchpoint.X) * (fdest.pt.X - forg.pt.X) +
                                   (fapex.pt.Y - searchpoint.Y) * (fdest.pt.Y - forg.pt.Y) > 0.0;
                    }
                    else
                    {
                        moveleft = true;
                    }
                }
                else
                {
                    if (orgorient > 0.0)
                    {
                        moveleft = false;
                    }
                    else
                    {
                        // The point we seek must be on the boundary of or inside this
                        // triangle.
                        if (destorient == 0.0)
                        {
                            searchtri.LprevSelf();
                            return LocateResult.OnEdge;
                        }
                        if (orgorient == 0.0)
                        {
                            searchtri.LnextSelf();
                            return LocateResult.OnEdge;
                        }
                        return LocateResult.InTriangle;
                    }
                }

                // Move to another triangle.  Leave a trace 'backtracktri' in case
                // floating-point roundoff or some such bogey causes us to walk
                // off a boundary of the triangulation.
                if (moveleft)
                {
                    searchtri.Lprev(ref backtracktri);
                    fdest = fapex;
                }
                else
                {
                    searchtri.Lnext(ref backtracktri);
                    forg = fapex;
                }
                backtracktri.Sym(ref searchtri);

                if (checksegments && stopatsubsegment)
                {
                    // Check for walking through a subsegment.
                    backtracktri.SegPivot(ref checkedge);
                    if (checkedge.ss != dummysub)
                    {
                        // Go back to the last triangle.
                        backtracktri.Copy(ref searchtri);
                        return LocateResult.Outside;
                    }
                }
                // Check for walking right out of the triangulation.
                if (searchtri.triangle == dummytri)
                {
                    // Go back to the last triangle.
                    backtracktri.Copy(ref searchtri);
                    return LocateResult.Outside;
                }

                fapex = searchtri.Apex();
            }
        }

        /// <summary>
        /// Find a triangle or edge containing a given point.
        /// </summary>
        /// <param name="searchpoint">The point to locate.</param>
        /// <param name="searchtri">The triangle to start the search at.</param>
        /// <returns>Location information.</returns>
        /// <remarks>
        /// Searching begins from one of:  the input 'searchtri', a recently
        /// encountered triangle 'recenttri', or from a triangle chosen from a
        /// random sample.  The choice is made by determining which triangle's
        /// origin is closest to the point we are searching for.  Normally,
        /// 'searchtri' should be a handle on the convex hull of the triangulation.
        ///
        /// Details on the random sampling method can be found in the Mucke, Saias,
        /// and Zhu paper cited in the header of this code.
        ///
        /// On completion, 'searchtri' is a triangle that contains 'searchpoint'.
        ///
        /// Returns ONVERTEX if the point lies on an existing vertex.  'searchtri'
        /// is a handle whose origin is the existing vertex.
        ///
        /// Returns ONEDGE if the point lies on a mesh edge.  'searchtri' is a
        /// handle whose primary edge is the edge on which the point lies.
        ///
        /// Returns INTRIANGLE if the point lies strictly within a triangle.
        /// 'searchtri' is a handle on the triangle that contains the point.
        ///
        /// Returns OUTSIDE if the point lies outside the mesh.  'searchtri' is a
        /// handle whose primary edge the point is to the right of.  This might
        /// occur when the circumcenter of a triangle falls just slightly outside
        /// the mesh due to floating-point roundoff error.  It also occurs when
        /// seeking a hole or region point that a foolish user has placed outside
        /// the mesh.
        ///
        /// WARNING:  This routine is designed for convex triangulations, and will
        /// not generally work after the holes and concavities have been carved.
        /// </remarks>
        internal LocateResult Locate(Point2 searchpoint, ref Otri searchtri)
        {
            Otri sampletri = default(Otri);
            Vertex torg, tdest;
            double searchdist, dist;
            double ahead;
            //long samplesperblock, totalsamplesleft, samplesleft;
            //long population, totalpopulation;

            // Record the distance from the suggested starting triangle to the
            // point we seek.
            torg = searchtri.Org();
            searchdist = (searchpoint.X - torg.pt.X) * (searchpoint.X - torg.pt.X) +
                         (searchpoint.Y - torg.pt.Y) * (searchpoint.Y - torg.pt.Y);

            // If a recently encountered triangle has been recorded and has not been
            // deallocated, test it as a good starting point.
            if (recenttri.triangle != null)
            {
                if (!Otri.IsDead(recenttri.triangle))
                {
                    torg = recenttri.Org();
                    if ((torg.pt.X == searchpoint.X) && (torg.pt.Y == searchpoint.Y))
                    {
                        recenttri.Copy(ref searchtri);
                        return LocateResult.OnVertex;
                    }
                    dist = (searchpoint.X - torg.pt.X) * (searchpoint.X - torg.pt.X) +
                           (searchpoint.Y - torg.pt.Y) * (searchpoint.Y - torg.pt.Y);
                    if (dist < searchdist)
                    {
                        recenttri.Copy(ref searchtri);
                        searchdist = dist;
                    }
                }
            }

            // TODO: Improve sampling.
            sampler.Update(this);
            int[] samples = sampler.GetSamples(this);

            foreach (var key in samples)
            {
                sampletri.triangle = this.triangles[key];
                if (!Otri.IsDead(sampletri.triangle))
                {
                    torg = sampletri.Org();
                    dist = (searchpoint.X - torg.pt.X) * (searchpoint.X - torg.pt.X) +
                           (searchpoint.Y - torg.pt.Y) * (searchpoint.Y - torg.pt.Y);
                    if (dist < searchdist)
                    {
                        sampletri.Copy(ref searchtri);
                        searchdist = dist;
                    }
                }
            }

            // Where are we?
            torg = searchtri.Org();
            tdest = searchtri.Dest();
            // Check the starting triangle's vertices.
            if ((torg.pt.X == searchpoint.X) && (torg.pt.Y == searchpoint.Y))
            {
                return LocateResult.OnVertex;
            }
            if ((tdest.pt.X == searchpoint.X) && (tdest.pt.Y == searchpoint.Y))
            {
                searchtri.LnextSelf();
                return LocateResult.OnVertex;
            }
            // Orient 'searchtri' to fit the preconditions of calling preciselocate().
            ahead = Primitives.CounterClockwise(torg.pt, tdest.pt, searchpoint);
            if (ahead < 0.0)
            {
                // Turn around so that 'searchpoint' is to the left of the
                // edge specified by 'searchtri'.
                searchtri.SymSelf();
            }
            else if (ahead == 0.0)
            {
                // Check if 'searchpoint' is between 'torg' and 'tdest'.
                if (((torg.pt.X < searchpoint.X) == (searchpoint.X < tdest.pt.X)) &&
                    ((torg.pt.Y < searchpoint.Y) == (searchpoint.Y < tdest.pt.Y)))
                {
                    return LocateResult.OnEdge;
                }
            }
            return PreciseLocate(searchpoint, ref searchtri, false);
        }

        #endregion

        #region Segment insertion

        /// <summary>
        /// Find the first triangle on the path from one point to another.
        /// </summary>
        /// <param name="searchtri"></param>
        /// <param name="searchpoint"></param>
        /// <returns>
        /// The return value notes whether the destination or apex of the found
        /// triangle is collinear with the two points in question.</returns>
        /// <remarks>
        /// Finds the triangle that intersects a line segment drawn from the
        /// origin of 'searchtri' to the point 'searchpoint', and returns the result
        /// in 'searchtri'. The origin of 'searchtri' does not change, even though
        /// the triangle returned may differ from the one passed in. This routine
        /// is used to find the direction to move in to get from one point to
        /// another.
        /// </remarks>
        FindDirectionResult FindDirection(ref Otri searchtri, Vertex searchpoint)
        {
            Otri checktri = default(Otri);
            Vertex startvertex;
            Vertex leftvertex, rightvertex;
            double leftccw, rightccw;
            bool leftflag, rightflag;

            startvertex = searchtri.Org();
            rightvertex = searchtri.Dest();
            leftvertex = searchtri.Apex();
            // Is 'searchpoint' to the left?
            leftccw = Primitives.CounterClockwise(searchpoint.pt, startvertex.pt, leftvertex.pt);
            leftflag = leftccw > 0.0;
            // Is 'searchpoint' to the right?
            rightccw = Primitives.CounterClockwise(startvertex.pt, searchpoint.pt, rightvertex.pt);
            rightflag = rightccw > 0.0;
            if (leftflag && rightflag)
            {
                // 'searchtri' faces directly away from 'searchpoint'. We could go left
                // or right. Ask whether it's a triangle or a boundary on the left.
                searchtri.Onext(ref checktri);
                if (checktri.triangle == Mesh.dummytri)
                {
                    leftflag = false;
                }
                else
                {
                    rightflag = false;
                }
            }
            while (leftflag)
            {
                // Turn left until satisfied.
                searchtri.OnextSelf();
                if (searchtri.triangle == Mesh.dummytri)
                {
                    logger.Error("Unable to find a triangle on path.", "Mesh.FindDirection().1");
                    throw new Exception("Unable to find a triangle on path.");
                }
                leftvertex = searchtri.Apex();
                rightccw = leftccw;
                leftccw = Primitives.CounterClockwise(searchpoint.pt, startvertex.pt, leftvertex.pt);
                leftflag = leftccw > 0.0;
            }
            while (rightflag)
            {
                // Turn right until satisfied.
                searchtri.OprevSelf();
                if (searchtri.triangle == Mesh.dummytri)
                {
                    logger.Error("Unable to find a triangle on path.", "Mesh.FindDirection().2");
                    throw new Exception("Unable to find a triangle on path.");
                }
                rightvertex = searchtri.Dest();
                leftccw = rightccw;
                rightccw = Primitives.CounterClockwise(startvertex.pt, searchpoint.pt, rightvertex.pt);
                rightflag = rightccw > 0.0;
            }
            if (leftccw == 0.0)
            {
                return FindDirectionResult.Leftcollinear;
            }
            else if (rightccw == 0.0)
            {
                return FindDirectionResult.Rightcollinear;
            }
            else
            {
                return FindDirectionResult.Within;
            }
        }

        /// <summary>
        /// Find the intersection of an existing segment and a segment that is being 
        /// inserted. Insert a vertex at the intersection, splitting an existing subsegment.
        /// </summary>
        /// <param name="splittri"></param>
        /// <param name="splitsubseg"></param>
        /// <param name="endpoint2"></param>
        /// <remarks>
        /// The segment being inserted connects the apex of splittri to endpoint2.
        /// splitsubseg is the subsegment being split, and MUST adjoin splittri.
        /// Hence, endpoints of the subsegment being split are the origin and
        /// destination of splittri.
        ///
        /// On completion, splittri is a handle having the newly inserted
        /// intersection point as its origin, and endpoint1 as its destination.
        /// </remarks>
        void SegmentIntersection(ref Otri splittri, ref Osub splitsubseg, Vertex endpoint2)
        {
            Osub opposubseg = default(Osub);
            Vertex endpoint1;
            Vertex torg, tdest;
            Vertex leftvertex, rightvertex;
            Vertex newvertex;
            InsertVertexResult success;
            FindDirectionResult collinear;
            double ex, ey;
            double tx, ty;
            double etx, ety;
            double split, denom;

            // Find the other three segment endpoints.
            endpoint1 = splittri.Apex();
            torg = splittri.Org();
            tdest = splittri.Dest();
            // Segment intersection formulae; see the Antonio reference.
            tx = tdest.pt.X - torg.pt.X;
            ty = tdest.pt.Y - torg.pt.Y;
            ex = endpoint2.pt.X - endpoint1.pt.X;
            ey = endpoint2.pt.Y - endpoint1.pt.Y;
            etx = torg.pt.X - endpoint2.pt.X;
            ety = torg.pt.Y - endpoint2.pt.Y;
            denom = ty * ex - tx * ey;
            if (denom == 0.0)
            {
                logger.Error("Attempt to find intersection of parallel segments.", 
                    "Mesh.SegmentIntersection()");
                throw new Exception("Attempt to find intersection of parallel segments.");
            }
            split = (ey * etx - ex * ety) / denom;
            // Create the new vertex.
            newvertex = new Vertex(nextras); //(vertex) poolalloc(&m.vertices);
            vertices.Add(newvertex.Hash, newvertex);
            // Interpolate its coordinate and attributes.
            //for (i = 0; i < 2 + nextras; i++) {
            newvertex.pt.X = torg.pt.X + split * (tdest.pt.X - torg.pt.X);
            newvertex.pt.Y = torg.pt.Y + split * (tdest.pt.Y - torg.pt.Y);
            
            newvertex.mark = splitsubseg.ss.boundary;
            newvertex.type = VertexType.InputVertex;

            // Insert the intersection vertex.  This should always succeed.
            success = InsertVertex(newvertex, ref splittri, ref splitsubseg, false, false);
            if (success != InsertVertexResult.Successful)
            {
                logger.Error("Failure to split a segment.", "Mesh.SegmentIntersection()");
                throw new Exception("Failure to split a segment.");
            }
            // Record a triangle whose origin is the new vertex.
            newvertex.tri = splittri;
            if (steinerleft > 0)
            {
                steinerleft--;
            }

            // Divide the segment into two, and correct the segment endpoints.
            splitsubseg.SymSelf();
            splitsubseg.Pivot(ref opposubseg);
            splitsubseg.Dissolve();
            opposubseg.Dissolve();
            do
            {
                splitsubseg.SetSegOrg(newvertex);
                splitsubseg.NextSelf();
            } while (splitsubseg.ss != Mesh.dummysub);
            do
            {
                opposubseg.SetSegOrg(newvertex);
                opposubseg.NextSelf();
            } while (opposubseg.ss != Mesh.dummysub);

            // Inserting the vertex may have caused edge flips.  We wish to rediscover
            // the edge connecting endpoint1 to the new intersection vertex.
            collinear = FindDirection(ref splittri, endpoint1);
            rightvertex = splittri.Dest();
            leftvertex = splittri.Apex();
            if ((leftvertex.pt.X == endpoint1.pt.X) && (leftvertex.pt.Y == endpoint1.pt.Y))
            {
                splittri.OnextSelf();
            }
            else if ((rightvertex.pt.X != endpoint1.pt.X) || (rightvertex.pt.Y != endpoint1.pt.Y))
            {
                logger.Error("Topological inconsistency after splitting a segment.", "Mesh.SegmentIntersection()");
                throw new Exception("Topological inconsistency after splitting a segment.");
            }
            // 'splittri' should have destination endpoint1.
        }

        /// <summary>
        /// Scout the first triangle on the path from one endpoint to another, and check 
        /// for completion (reaching the second endpoint), a collinear vertex, or the 
        /// intersection of two segments.
        /// </summary>
        /// <param name="searchtri"></param>
        /// <param name="endpoint2"></param>
        /// <param name="newmark"></param>
        /// <returns>Returns one if the entire segment is successfully inserted, and zero 
        /// if the job must be finished by conformingedge() or constrainededge().</returns>
        /// <remarks>
        /// If the first triangle on the path has the second endpoint as its
        /// destination or apex, a subsegment is inserted and the job is done.
        ///
        /// If the first triangle on the path has a destination or apex that lies on
        /// the segment, a subsegment is inserted connecting the first endpoint to
        /// the collinear vertex, and the search is continued from the collinear
        /// vertex.
        ///
        /// If the first triangle on the path has a subsegment opposite its origin,
        /// then there is a segment that intersects the segment being inserted.
        /// Their intersection vertex is inserted, splitting the subsegment.
        /// </remarks>
        bool ScoutSegment(ref Otri searchtri, Vertex endpoint2, int newmark)
        {
            Otri crosstri = default(Otri);
            Osub crosssubseg = default(Osub);
            Vertex leftvertex, rightvertex;
            FindDirectionResult collinear;

            collinear = FindDirection(ref searchtri, endpoint2);
            rightvertex = searchtri.Dest();
            leftvertex = searchtri.Apex();
            if (((leftvertex.pt.X == endpoint2.pt.X) && (leftvertex.pt.Y == endpoint2.pt.Y)) ||
                ((rightvertex.pt.X == endpoint2.pt.X) && (rightvertex.pt.Y == endpoint2.pt.Y)))
            {
                // The segment is already an edge in the mesh.
                if ((leftvertex.pt.X == endpoint2.pt.X) && (leftvertex.pt.Y == endpoint2.pt.Y))
                {
                    searchtri.LprevSelf();
                }
                // Insert a subsegment, if there isn't already one there.
                InsertSubseg(ref searchtri, newmark);
                return true;
            }
            else if (collinear == FindDirectionResult.Leftcollinear)
            {
                // We've collided with a vertex between the segment's endpoints.
                // Make the collinear vertex be the triangle's origin.
                searchtri.LprevSelf();
                InsertSubseg(ref searchtri, newmark);
                // Insert the remainder of the segment.
                return ScoutSegment(ref searchtri, endpoint2, newmark);
            }
            else if (collinear == FindDirectionResult.Rightcollinear)
            {
                // We've collided with a vertex between the segment's endpoints.
                InsertSubseg(ref searchtri, newmark);
                // Make the collinear vertex be the triangle's origin.
                searchtri.LnextSelf();
                // Insert the remainder of the segment.
                return ScoutSegment(ref searchtri, endpoint2, newmark);
            }
            else
            {
                searchtri.Lnext(ref crosstri);
                crosstri.SegPivot(ref crosssubseg);
                // Check for a crossing segment.
                if (crosssubseg.ss == Mesh.dummysub)
                {
                    return false;
                }
                else
                {
                    // Insert a vertex at the intersection.
                    SegmentIntersection(ref crosstri, ref crosssubseg, endpoint2);
                    crosstri.Copy(ref searchtri);
                    InsertSubseg(ref searchtri, newmark);
                    // Insert the remainder of the segment.
                    return ScoutSegment(ref searchtri, endpoint2, newmark);
                }
            }
        }

        /*
        /// <summary>
        /// Force a segment into a conforming Delaunay triangulation by inserting a 
        /// vertex at its midpoint, and recursively forcing in the two half-segments 
        /// if necessary.
        /// </summary>
        /// <param name="endpoint1"></param>
        /// <param name="endpoint2"></param>
        /// <param name="newmark"></param>
        /// <remarks>
        /// Generates a sequence of subsegments connecting 'endpoint1' to
        /// 'endpoint2'.  'newmark' is the boundary marker of the segment, assigned
        /// to each new splitting vertex and subsegment.
        ///
        /// Note that conformingedge() does not always maintain the conforming
        /// Delaunay property.  Once inserted, segments are locked into place;
        /// vertices inserted later (to force other segments in) may render these
        /// fixed segments non-Delaunay.  The conforming Delaunay property will be
        /// restored by enforcequality() by splitting encroached subsegments.
        /// </remarks>
        void ConformingEdge(Vertex endpoint1, Vertex endpoint2, int newmark)
        {
            Otri searchtri1 = default(Otri), searchtri2 = default(Otri);
            Osub brokensubseg = default(Osub);
            Vertex newvertex;
            Vertex midvertex1, midvertex2;
            InsertVertexResult success;
            int i;

            // Create a new vertex to insert in the middle of the segment.
            //newvertex = (vertex) poolalloc(&m.vertices);
            newvertex = new Vertex(nextras);
            vertices.Add(newvertex.Hash, newvertex);

            // Interpolate coordinates and attributes.
            for (i = 0; i < 2 + nextras; i++)
            {
                //newvertex.pt[i] = 0.5 * (endpoint1.pt[i] + endpoint2.pt[i]);
            }
            newvertex.mark = newmark;
            newvertex.type = VertexType.SegmentVertex;
            // No known triangle to search from.
            searchtri1.triangle = Mesh.dummytri;
            // Attempt to insert the new vertex.
            Osub tmp = default(Osub);
            success = InsertVertex(newvertex, ref searchtri1, ref tmp, false, false);
            if (success == InsertVertexResult.Duplicate)
            {
                // Segment intersects existing vertex. Use the vertex that's already there.
                VertexDealloc(newvertex);
                newvertex = searchtri1.Org();
            }
            else
            {
                if (success == InsertVertexResult.Violating)
                {
                    // Two segments intersect. By fluke, we've landed right on another segment. Split it.
                    searchtri1.SegPivot(ref brokensubseg);
                    success = InsertVertex(newvertex, ref searchtri1, ref brokensubseg, false, false);
                    if (success != InsertVertexResult.Successful)
                    {
                        logger.Error("Failure to split a segment.", "Mesh.ConformingEdge()");
                        throw new Exception("Failure to split a segment.");
                    }
                }
                // The vertex has been inserted successfully.
                if (steinerleft > 0)
                {
                    steinerleft--;
                }
            }
            searchtri1.Copy(ref searchtri2);
            // 'searchtri1' and 'searchtri2' are fastened at their origins to
            // 'newvertex', and will be directed toward 'endpoint1' and 'endpoint2'
            // respectively. First, we must get 'searchtri2' out of the way so it
            // won't be invalidated during the insertion of the first half of the
            // segment.
            FindDirection(ref searchtri2, endpoint2);
            if (!ScoutSegment(ref searchtri1, endpoint1, newmark))
            {
                // The origin of searchtri1 may have changed if a collision with an
                // intervening vertex on the segment occurred.
                midvertex1 = searchtri1.Org();
                ConformingEdge(midvertex1, endpoint1, newmark);
            }
            if (!ScoutSegment(ref searchtri2, endpoint2, newmark))
            {
                // The origin of searchtri2 may have changed if a collision with an
                // intervening vertex on the segment occurred.
                midvertex2 = searchtri2.Org();
                ConformingEdge(midvertex2, endpoint2, newmark);
            }
        }
         * */

        /// <summary>
        /// Enforce the Delaunay condition at an edge, fanning out recursively from 
        /// an existing vertex. Pay special attention to stacking inverted triangles.
        /// </summary>
        /// <param name="fixuptri"></param>
        /// <param name="leftside">indicates whether or not fixuptri is to the left of 
        /// the segment being inserted.  (Imagine that the segment is pointing up from
        /// endpoint1 to endpoint2.)</param>
        /// <remarks>
        /// This is a support routine for inserting segments into a constrained
        /// Delaunay triangulation.
        ///
        /// The origin of fixuptri is treated as if it has just been inserted, and
        /// the local Delaunay condition needs to be enforced.  It is only enforced
        /// in one sector, however, that being the angular range defined by
        /// fixuptri.
        ///
        /// This routine also needs to make decisions regarding the "stacking" of
        /// triangles.  (Read the description of constrainededge() below before
        /// reading on here, so you understand the algorithm.)  If the position of
        /// the new vertex (the origin of fixuptri) indicates that the vertex before
        /// it on the polygon is a reflex vertex, then "stack" the triangle by
        /// doing nothing.  (fixuptri is an inverted triangle, which is how stacked
        /// triangles are identified.)
        ///
        /// Otherwise, check whether the vertex before that was a reflex vertex.
        /// If so, perform an edge flip, thereby eliminating an inverted triangle
        /// (popping it off the stack).  The edge flip may result in the creation
        /// of a new inverted triangle, depending on whether or not the new vertex
        /// is visible to the vertex three edges behind on the polygon.
        ///
        /// If neither of the two vertices behind the new vertex are reflex
        /// vertices, fixuptri and fartri, the triangle opposite it, are not
        /// inverted; hence, ensure that the edge between them is locally Delaunay.
        /// </remarks>
        void DelaunayFixup(ref Otri fixuptri, bool leftside)
        {
            Otri neartri = default(Otri);
            Otri fartri = default(Otri);
            Osub faredge = default(Osub);
            Vertex nearvertex, leftvertex, rightvertex, farvertex;

            fixuptri.Lnext(ref neartri);
            neartri.Sym(ref fartri);
            // Check if the edge opposite the origin of fixuptri can be flipped.
            if (fartri.triangle == Mesh.dummytri)
            {
                return;
            }
            neartri.SegPivot(ref faredge);
            if (faredge.ss != Mesh.dummysub)
            {
                return;
            }
            // Find all the relevant vertices.
            nearvertex = neartri.Apex();
            leftvertex = neartri.Org();
            rightvertex = neartri.Dest();
            farvertex = fartri.Apex();
            // Check whether the previous polygon vertex is a reflex vertex.
            if (leftside)
            {
                if (Primitives.CounterClockwise(nearvertex.pt, leftvertex.pt, farvertex.pt) <= 0.0)
                {
                    // leftvertex is a reflex vertex too. Nothing can
                    // be done until a convex section is found.
                    return;
                }
            }
            else
            {
                if (Primitives.CounterClockwise(farvertex.pt, rightvertex.pt, nearvertex.pt) <= 0.0)
                {
                    // rightvertex is a reflex vertex too.  Nothing can
                    // be done until a convex section is found.
                    return;
                }
            }
            if (Primitives.CounterClockwise(rightvertex.pt, leftvertex.pt, farvertex.pt) > 0.0)
            {
                // fartri is not an inverted triangle, and farvertex is not a reflex
                // vertex.  As there are no reflex vertices, fixuptri isn't an
                // inverted triangle, either.  Hence, test the edge between the
                // triangles to ensure it is locally Delaunay.
                if (Primitives.InCircle(leftvertex.pt, farvertex.pt, rightvertex.pt, nearvertex.pt) <= 0.0)
                {
                    return;
                }
                // Not locally Delaunay; go on to an edge flip.
            }
            // else fartri is inverted; remove it from the stack by flipping.
            Flip(ref neartri);
            fixuptri.LprevSelf();    // Restore the origin of fixuptri after the flip.
            // Recursively process the two triangles that result from the flip.
            DelaunayFixup(ref fixuptri, leftside);
            DelaunayFixup(ref fartri, leftside);
        }

        /// <summary>
        /// Force a segment into a constrained Delaunay triangulation by deleting the 
        /// triangles it intersects, and triangulating the polygons that form on each 
        /// side of it.
        /// </summary>
        /// <param name="starttri"></param>
        /// <param name="endpoint2"></param>
        /// <param name="newmark"></param>
        /// <remarks>
        /// Generates a single subsegment connecting 'endpoint1' to 'endpoint2'.
        /// The triangle 'starttri' has 'endpoint1' as its origin.  'newmark' is the
        /// boundary marker of the segment.
        ///
        /// To insert a segment, every triangle whose interior intersects the
        /// segment is deleted.  The union of these deleted triangles is a polygon
        /// (which is not necessarily monotone, but is close enough), which is
        /// divided into two polygons by the new segment.  This routine's task is
        /// to generate the Delaunay triangulation of these two polygons.
        ///
        /// You might think of this routine's behavior as a two-step process.  The
        /// first step is to walk from endpoint1 to endpoint2, flipping each edge
        /// encountered.  This step creates a fan of edges connected to endpoint1,
        /// including the desired edge to endpoint2.  The second step enforces the
        /// Delaunay condition on each side of the segment in an incremental manner:
        /// proceeding along the polygon from endpoint1 to endpoint2 (this is done
        /// independently on each side of the segment), each vertex is "enforced"
        /// as if it had just been inserted, but affecting only the previous
        /// vertices.  The result is the same as if the vertices had been inserted
        /// in the order they appear on the polygon, so the result is Delaunay.
        ///
        /// In truth, constrainededge() interleaves these two steps.  The procedure
        /// walks from endpoint1 to endpoint2, and each time an edge is encountered
        /// and flipped, the newly exposed vertex (at the far end of the flipped
        /// edge) is "enforced" upon the previously flipped edges, usually affecting
        /// only one side of the polygon (depending upon which side of the segment
        /// the vertex falls on).
        ///
        /// The algorithm is complicated by the need to handle polygons that are not
        /// convex.  Although the polygon is not necessarily monotone, it can be
        /// triangulated in a manner similar to the stack-based algorithms for
        /// monotone polygons.  For each reflex vertex (local concavity) of the
        /// polygon, there will be an inverted triangle formed by one of the edge
        /// flips.  (An inverted triangle is one with negative area - that is, its
        /// vertices are arranged in clockwise order - and is best thought of as a
        /// wrinkle in the fabric of the mesh.)  Each inverted triangle can be
        /// thought of as a reflex vertex pushed on the stack, waiting to be fixed
        /// later.
        ///
        /// A reflex vertex is popped from the stack when a vertex is inserted that
        /// is visible to the reflex vertex.  (However, if the vertex behind the
        /// reflex vertex is not visible to the reflex vertex, a new inverted
        /// triangle will take its place on the stack.)  These details are handled
        /// by the delaunayfixup() routine above.
        /// </remarks>
        void ConstrainedEdge(ref Otri starttri, Vertex endpoint2, int newmark)
        {
            Otri fixuptri = default(Otri), fixuptri2 = default(Otri);
            Osub crosssubseg = default(Osub);
            Vertex endpoint1;
            Vertex farvertex;
            double area;
            bool collision;
            bool done;

            endpoint1 = starttri.Org();
            starttri.Lnext(ref fixuptri);
            Flip(ref fixuptri);
            // 'collision' indicates whether we have found a vertex directly
            // between endpoint1 and endpoint2.
            collision = false;
            done = false;
            do
            {
                farvertex = fixuptri.Org();
                // 'farvertex' is the extreme point of the polygon we are "digging"
                //   to get from endpoint1 to endpoint2.
                if ((farvertex.pt.X == endpoint2.pt.X) && (farvertex.pt.Y == endpoint2.pt.Y))
                {
                    fixuptri.Oprev(ref fixuptri2);
                    // Enforce the Delaunay condition around endpoint2.
                    DelaunayFixup(ref fixuptri, false);
                    DelaunayFixup(ref fixuptri2, true);
                    done = true;
                }
                else
                {
                    // Check whether farvertex is to the left or right of the segment
                    //   being inserted, to decide which edge of fixuptri to dig
                    //   through next.
                    area = Primitives.CounterClockwise(endpoint1.pt, endpoint2.pt, farvertex.pt);
                    if (area == 0.0)
                    {
                        // We've collided with a vertex between endpoint1 and endpoint2.
                        collision = true;
                        fixuptri.Oprev(ref fixuptri2);
                        // Enforce the Delaunay condition around farvertex.
                        DelaunayFixup(ref fixuptri, false);
                        DelaunayFixup(ref fixuptri2, true);
                        done = true;
                    }
                    else
                    {
                        if (area > 0.0)
                        {        // farvertex is to the left of the segment.
                            fixuptri.Oprev(ref fixuptri2);
                            // Enforce the Delaunay condition around farvertex, on the
                            // left side of the segment only.
                            DelaunayFixup(ref fixuptri2, true);
                            // Flip the edge that crosses the segment. After the edge is
                            // flipped, one of its endpoints is the fan vertex, and the
                            // destination of fixuptri is the fan vertex.
                            fixuptri.LprevSelf();
                        }
                        else
                        {                // farvertex is to the right of the segment.
                            DelaunayFixup(ref fixuptri, false);
                            // Flip the edge that crosses the segment. After the edge is
                            // flipped, one of its endpoints is the fan vertex, and the
                            // destination of fixuptri is the fan vertex.
                            fixuptri.OprevSelf();
                        }
                        // Check for two intersecting segments.
                        fixuptri.SegPivot(ref crosssubseg);
                        if (crosssubseg.ss == Mesh.dummysub)
                        {
                            Flip(ref fixuptri);    // May create inverted triangle at left.
                        }
                        else
                        {
                            // We've collided with a segment between endpoint1 and endpoint2.
                            collision = true;
                            // Insert a vertex at the intersection.
                            SegmentIntersection(ref fixuptri, ref crosssubseg, endpoint2);
                            done = true;
                        }
                    }
                }
            } while (!done);
            // Insert a subsegment to make the segment permanent.
            InsertSubseg(ref fixuptri, newmark);
            // If there was a collision with an interceding vertex, install another
            // segment connecting that vertex with endpoint2.
            if (collision)
            {
                // Insert the remainder of the segment.
                if (!ScoutSegment(ref fixuptri, endpoint2, newmark))
                {
                    ConstrainedEdge(ref fixuptri, endpoint2, newmark);
                }
            }
        }

        /// <summary>
        /// Insert a PSLG segment into a triangulation.
        /// </summary>
        /// <param name="endpoint1"></param>
        /// <param name="endpoint2"></param>
        /// <param name="newmark"></param>
        void InsertSegment(Vertex endpoint1, Vertex endpoint2, int newmark)
        {
            Otri searchtri1 = default(Otri), searchtri2 = default(Otri);
            Vertex checkvertex = null;

            // Find a triangle whose origin is the segment's first endpoint.
            searchtri1 = endpoint1.tri;
            if (searchtri1.triangle != null)
            {
                checkvertex = searchtri1.Org();
            }

            if (checkvertex != endpoint1)
            {
                // Find a boundary triangle to search from.
                searchtri1.triangle = Mesh.dummytri;
                searchtri1.orient = 0;
                searchtri1.SymSelf();
                // Search for the segment's first endpoint by point location.
                if (Locate(endpoint1.pt, ref searchtri1) != LocateResult.OnVertex)
                {
                    logger.Error("Unable to locate PSLG vertex in triangulation.", "Mesh.InsertSegment().1");
                    throw new Exception("Unable to locate PSLG vertex in triangulation.");
                }
            }
            // Remember this triangle to improve subsequent point location.
            searchtri1.Copy(ref recenttri);

            // Scout the beginnings of a path from the first endpoint
            // toward the second.
            if (ScoutSegment(ref searchtri1, endpoint2, newmark))
            {
                // The segment was easily inserted.
                return;
            }
            // The first endpoint may have changed if a collision with an intervening
            // vertex on the segment occurred.
            endpoint1 = searchtri1.Org();

            // Find a triangle whose origin is the segment's second endpoint.
            checkvertex = null;
            searchtri2 = endpoint2.tri;
            if (searchtri2.triangle != null)
            {
                checkvertex = searchtri2.Org();
            }
            if (checkvertex != endpoint2)
            {
                // Find a boundary triangle to search from.
                searchtri2.triangle = Mesh.dummytri;
                searchtri2.orient = 0;
                searchtri2.SymSelf();
                // Search for the segment's second endpoint by point location.
                if (Locate(endpoint2.pt, ref searchtri2) != LocateResult.OnVertex)
                {
                    logger.Error("Unable to locate PSLG vertex in triangulation.", "Mesh.InsertSegment().2");
                    throw new Exception("Unable to locate PSLG vertex in triangulation.");
                }
            }
            // Remember this triangle to improve subsequent point location.
            searchtri2.Copy(ref recenttri);
            // Scout the beginnings of a path from the second endpoint
            // toward the first.
            if (ScoutSegment(ref searchtri2, endpoint1, newmark))
            {
                // The segment was easily inserted.
                return;
            }
            // The second endpoint may have changed if a collision with an intervening
            // vertex on the segment occurred.
            endpoint2 = searchtri2.Org();

            //if (Behavior.SplitSeg)
            //{
            //    // Insert vertices to force the segment into the triangulation.
            //    ConformingEdge(endpoint1, endpoint2, newmark);
            //}
            //else

            // Insert the segment directly into the triangulation.
            ConstrainedEdge(ref searchtri1, endpoint2, newmark);
        }

        /// <summary>
        /// Cover the convex hull of a triangulation with subsegments.
        /// </summary>
        void MarkHull()
        {
            Otri hulltri = default(Otri);
            Otri nexttri = default(Otri);
            Otri starttri = default(Otri);

            // Find a triangle handle on the hull.
            hulltri.triangle = Mesh.dummytri;
            hulltri.orient = 0;
            hulltri.SymSelf();
            // Remember where we started so we know when to stop.
            hulltri.Copy(ref starttri);
            // Go once counterclockwise around the convex hull.
            do
            {
                // Create a subsegment if there isn't already one here.
                InsertSubseg(ref hulltri, 1);
                // To find the next hull edge, go clockwise around the next vertex.
                hulltri.LnextSelf();
                hulltri.Oprev(ref nexttri);
                while (nexttri.triangle != Mesh.dummytri)
                {
                    nexttri.Copy(ref hulltri);
                    hulltri.Oprev(ref nexttri);
                }
            } while (!hulltri.Equal(starttri));
        }

        /// <summary>
        /// Create the segments of a triangulation, including PSLG segments and edges 
        /// on the convex hull.
        /// </summary>
        /// <param name="segmentlist"></param>
        /// <param name="segmentmarkerlist"></param>
        /// <param name="numberofsegments"></param>
        void FormSkeleton(MeshData data)
        {
            Vertex endpoint1, endpoint2;
            bool segmentmarkers;
            int end1, end2;
            int boundmarker;
            int numberofsegments = data.Segments == null ? 0 : data.Segments.Length;

            if (Behavior.Poly)
            {
                insegments = numberofsegments;
                segmentmarkers = data.SegmentMarkers != null;

                // If the input vertices are collinear, there is no triangulation,
                // so don't try to insert segments.
                if (triangles.Count == 0)
                {
                    return;
                }

                // If segments are to be inserted, compute a mapping
                // from vertices to triangles.
                if (insegments > 0)
                {
                    MakeVertexMap();
                }

                boundmarker = 0;
                // Read and insert the segments.
                for (int i = 0; i < insegments; i++)
                {
                    end1 = data.Segments[i][0];
                    end2 = data.Segments[i][1];
                    if (segmentmarkers)
                    {
                        boundmarker = data.SegmentMarkers[i];
                    }

                    if ((end1 < 0) || (end1 >= invertices))
                    {
                        if (Behavior.Verbose)
                        {
                            logger.Warning("Invalid first endpoint of segment.", "Mesh.FormSkeleton().1");
                        }
                    }
                    else if ((end2 < 0) || (end2 >= invertices))
                    {
                        if (Behavior.Verbose)
                        {
                            logger.Warning("Invalid second endpoint of segment.", "Mesh.FormSkeleton().2");
                        }
                    }
                    else
                    {
                        // TODO: Is using the vertex ID reliable???
                        // It should be. The ID gets appropriately set in TransferNodes().

                        // Find the vertices numbered 'end1' and 'end2'.
                        endpoint1 = vertices[end1]; // getvertex(end1);
                        endpoint2 = vertices[end2]; // getvertex(end2);
                        if ((endpoint1.pt.X == endpoint2.pt.X) && (endpoint1.pt.Y == endpoint2.pt.Y))
                        {
                            if (Behavior.Verbose)
                            {
                                logger.Warning("Endpoints of segments are coincident.", "Mesh.FormSkeleton()");
                            }
                        }
                        else
                        {
                            InsertSegment(endpoint1, endpoint2, boundmarker);
                        }
                    }
                }
            }
            else
            {
                insegments = 0;
            }

            if (Behavior.Convex || !Behavior.Poly)
            {
                // Enclose the convex hull with subsegments.
                MarkHull();
            }
        }

        #endregion

        #region Dealloc

        /// <summary>
        /// Deallocate space for a triangle, marking it dead.
        /// </summary>
        /// <param name="dyingtriangle"></param>
        internal void TriangleDealloc(Triangle dyingtriangle)
        {
            // Mark the triangle as dead.  This makes it possible to detect dead 
            // triangles when traversing the list of all triangles.
            Otri.Kill(dyingtriangle);
            triangles.Remove(dyingtriangle.Hash);
        }

        /// <summary>
        /// Deallocate space for a vertex, marking it dead.
        /// </summary>
        /// <param name="dyingvertex"></param>
        internal void VertexDealloc(Vertex dyingvertex)
        {
            // Mark the vertex as dead.  This makes it possible to detect dead 
            // vertices when traversing the list of all vertices.
            dyingvertex.type = VertexType.DeadVertex;
            vertices.Remove(dyingvertex.Hash);
        }

        /// <summary>
        /// Deallocate space for a subsegment, marking it dead.
        /// </summary>
        /// <param name="dyingsubseg"></param>
        internal void SubsegDealloc(Subseg dyingsubseg)
        {
            // Mark the subsegment as dead.  This makes it possible to detect dead 
            // subsegments when traversing the list of all subsegments.
            Osub.Kill(dyingsubseg);
            subsegs.Remove(dyingsubseg.Hash);
        }

        #endregion
    }
}
