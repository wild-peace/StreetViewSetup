using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using NetTopologySuite;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using Coordinate = NetTopologySuite.Geometries.Coordinate;
// 使用别名解决 Point 类型冲突
using NTSPoint = NetTopologySuite.Geometries.Point;
using DrawingPoint = System.Drawing.Point;
using GeoAPI.Geometries;

namespace LocalStreetViewApp
{
    #region 数据模型

    /// <summary>
    /// 街景节点数据结构（点要素）
    /// </summary>
    public class StreetNode
    {
        public int Id;
        public double Lon; // 经度
        public double Lat; // 纬度

        /// <summary>
        /// 绑定的全景图片绝对路径
        /// </summary>
        public string ImagePath;

        /// <summary>
        /// 节点坐标与图片文件名坐标的匹配误差距离
        /// </summary>
        public double DistanceToImage;
    }

    /// <summary>
    /// 道路线数据结构（线要素）
    /// </summary>
    public class StreetLine
    {
        public int Id;
        /// <summary>
        /// 构成线段的坐标点序列
        /// </summary>
        public NetTopologySuite.Geometries.Coordinate[] Coordinates;
    }

    #endregion

    /// <summary>
    /// 地图核心管理器
    /// 负责数据的加载、坐标转换、数学计算以及 GDI+ 渲染
    /// </summary>
    public class MapManager
    {
        #region 成员变量

        public List<StreetNode> Nodes = new List<StreetNode>();
        public List<StreetLine> Lines = new List<StreetLine>();

        // 视图状态控制
        private double zoomLevel = 1.0;
        private double viewCenterX = 0;
        private double viewCenterY = 0;
        private bool isViewInitialized = false;

        #endregion

        #region 1. 文件加载 (IO)

        /// <summary>
        /// 加载点要素 Shapefile 文件
        /// </summary>
        /// <param name="shpPath">.shp 文件路径</param>
        public void LoadShapefile(string shpPath)
        {
            try
            {
                Nodes.Clear();
                Console.WriteLine($"[SHP加载] 开始读取: {shpPath}");

                if (!File.Exists(shpPath))
                {
                    Console.WriteLine("[错误] SHP文件不存在");
                    return;
                }

                // 检查必要的关联文件 (.shx, .dbf)
                string directory = Path.GetDirectoryName(shpPath);
                string baseName = Path.GetFileNameWithoutExtension(shpPath);
                string shxPath = Path.Combine(directory, baseName + ".shx");
                string dbfPath = Path.Combine(directory, baseName + ".dbf");

                if (!File.Exists(shxPath) || !File.Exists(dbfPath))
                {
                    Console.WriteLine("[错误] 缺少 .shx 或 .dbf 索引/属性文件");
                    return;
                }

                using (var reader = new ShapefileDataReader(shpPath, GeometryFactory.Default))
                {
                    int id = 0;
                    int recordCount = 0;

                    // 1. 扫描属性表头，用于后续智能匹配经纬度字段
                    var header = reader.DbaseHeader;

                    Console.WriteLine($"[SHP加载] 开始遍历要素...");

                    while (reader.Read())
                    {
                        recordCount++;
                        var geom = reader.Geometry as NTSPoint;
                        if (geom == null) continue; // 跳过非点要素

                        // 默认使用几何体的坐标
                        double lon = geom.X;
                        double lat = geom.Y;

                        // 2. 尝试从属性表读取更精确的经纬度（覆盖默认几何坐标）
                        // 兼容多种常见的字段命名方式
                        bool hasLonField = false;
                        bool hasLatField = false;
                        string[] possibleLonFields = { "lon", "longitude", "x", "经度", "long" };
                        string[] possibleLatFields = { "lat", "latitude", "y", "纬度", "lati" };

                        // 匹配经度字段
                        foreach (var fieldName in possibleLonFields)
                        {
                            try
                            {
                                var value = reader[fieldName];
                                if (value != null && double.TryParse(value.ToString(), out double parsedLon))
                                {
                                    lon = parsedLon;
                                    hasLonField = true;
                                    break;
                                }
                            }
                            catch { /* 忽略不存在的字段 */ }
                        }

                        // 匹配纬度字段
                        foreach (var fieldName in possibleLatFields)
                        {
                            try
                            {
                                var value = reader[fieldName];
                                if (value != null && double.TryParse(value.ToString(), out double parsedLat))
                                {
                                    lat = parsedLat;
                                    hasLatField = true;
                                    break;
                                }
                            }
                            catch { /* 忽略不存在的字段 */ }
                        }

                        if (!hasLonField || !hasLatField)
                        {
                            // 如果属性表中没找到，通常使用 Geometry 的坐标也是安全的
                            // Console.WriteLine($"[警告] 记录 {recordCount} 未找到属性字段，使用几何坐标");
                        }

                        Nodes.Add(new StreetNode
                        {
                            Id = id++,
                            Lon = lon,
                            Lat = lat,
                            ImagePath = null,
                            DistanceToImage = double.MaxValue
                        });
                    }
                    Console.WriteLine($"[SHP加载] 完成，共加载 {Nodes.Count} 个点");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SHP异常] {ex.Message}");
            }
        }

        /// <summary>
        /// 加载线要素 Shapefile 文件（路网）
        /// </summary>
        public void LoadLineShapefile(string shpPath)
        {
            try
            {
                Console.WriteLine($"[Line加载] 开始读取: {shpPath}");

                if (!File.Exists(shpPath)) return;

                using (var reader = new ShapefileDataReader(shpPath, GeometryFactory.Default))
                {
                    int id = 0;
                    int successCount = 0;

                    while (reader.Read())
                    {
                        var geometry = reader.Geometry;
                        if (geometry == null) continue;

                        // 处理 MultiLineString 或 LineString
                        for (int i = 0; i < geometry.NumGeometries; i++)
                        {
                            var part = geometry.GetGeometryN(i);

                            if (part.Coordinates != null && part.Coordinates.Length > 1)
                            {
                                // 避免 NetTopologySuite 与 GeoAPI 版本不一致导致的类型转换错误
                                var pointList = new List<Coordinate>();
                                foreach (var rawPoint in part.Coordinates)
                                {
                                    pointList.Add(new Coordinate(rawPoint.X, rawPoint.Y));
                                }

                                Lines.Add(new StreetLine
                                {
                                    Id = id++,
                                    Coordinates = pointList.ToArray()
                                });
                                successCount++;
                            }
                        }
                    }
                    Console.WriteLine($"[Line加载] 完成，共加载 {successCount} 条线段");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Line异常] {ex.Message}");
            }
        }

        /// <summary>
        /// 自动绑定图片：扫描文件夹下的 JPG，根据文件名中的坐标匹配最近的节点
        /// </summary>
        /// <param name="folder">包含图片的文件夹路径</param>
        /// <returns>成功绑定的数量</returns>
        public int AutoBindImages(string folder)
        {
            try
            {
                Console.WriteLine($"[图片绑定] 扫描文件夹: {folder}");
                if (!Directory.Exists(folder)) return 0;

                // 1. 获取并预解析所有图片
                var files = Directory.GetFiles(folder, "*.jpg", SearchOption.AllDirectories);
                var imageMetaList = new List<(double Lon, double Lat, string Path)>();

                foreach (var file in files)
                {
                    try
                    {
                        // 假设文件名格式: 经度_纬度.jpg (例如 120.123_30.456.jpg)
                        string fileName = Path.GetFileNameWithoutExtension(file);
                        var parts = fileName.Split('_');

                        if (parts.Length >= 2 &&
                            double.TryParse(parts[0], out double imgLon) &&
                            double.TryParse(parts[1], out double imgLat))
                        {
                            imageMetaList.Add((imgLon, imgLat, file));
                        }
                    }
                    catch { /* 忽略命名不规范的文件 */ }
                }

                Console.WriteLine($"[图片绑定] 解析出 {imageMetaList.Count} 张带坐标图片");

                // 2. 空间匹配 (最近邻搜索)
                int count = 0;
                // 容差: 0.00002 度 ≈ 2米
                double tolerance = 0.00002;

                foreach (var node in Nodes)
                {
                    // 寻找曼哈顿距离最近且在容差范围内的图片
                    var bestMatch = imageMetaList
                        .Select(img => new
                        {
                            Info = img,
                            Diff = Math.Abs(img.Lon - node.Lon) + Math.Abs(img.Lat - node.Lat)
                        })
                        .Where(x => x.Diff < tolerance)
                        .OrderBy(x => x.Diff)
                        .FirstOrDefault();

                    if (bestMatch != null)
                    {
                        node.ImagePath = bestMatch.Info.Path;
                        node.DistanceToImage = bestMatch.Diff;
                        count++;
                    }
                }

                Console.WriteLine($"[图片绑定] 成功绑定: {count} 张");
                return count;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[图片绑定异常] {ex.Message}");
                return 0;
            }
        }

        #endregion

        #region 2. 视图控制 (View Control)

        /// <summary>
        /// 平移地图
        /// </summary>
        /// <param name="screenDx">屏幕 X 轴位移像素</param>
        /// <param name="screenDy">屏幕 Y 轴位移像素</param>
        public void Pan(double screenDx, double screenDy, double canvasWidth, double canvasHeight)
        {
            if (!isViewInitialized) ResetView();

            var currentBounds = GetCurrentViewBounds();

            // 计算像素到地理单位的比例
            double ratioX = currentBounds.Width / canvasWidth;
            double ratioY = currentBounds.Height / canvasHeight;

            // 更新视野中心 (注意：地理坐标系Y轴通常向上，屏幕向下，需根据具体绘制逻辑调整正负)
            viewCenterX -= screenDx * ratioX;
            viewCenterY += screenDy * ratioY;
        }

        /// <summary>
        /// 以指定屏幕点为中心进行缩放
        /// </summary>
        /// <param name="delta">鼠标滚轮值</param>
        /// <param name="screenX">鼠标屏幕 X 坐标</param>
        /// <param name="screenY">鼠标屏幕 Y 坐标</param>
        public void ZoomAtPoint(int delta, double screenX, double screenY, double canvasWidth, double canvasHeight)
        {
            if (!isViewInitialized) ResetView();

            var currentBounds = GetCurrentViewBounds();
            double ratioX = currentBounds.Width / canvasWidth;
            double ratioY = currentBounds.Height / canvasHeight;

            // 1. 计算鼠标指向的地理位置
            double mouseGeoX = currentBounds.MinX + screenX * ratioX;
            double mouseGeoY = currentBounds.MaxY - screenY * ratioY; // Y轴反转

            // 2. 计算新缩放比例
            double scaleFactor = (delta > 0) ? 1.2 : (1.0 / 1.2);
            double newZoom = zoomLevel * scaleFactor;

            // 限制缩放范围
            if (newZoom < 0.1) newZoom = 0.1;
            if (newZoom > 10000.0) newZoom = 10000.0;
            if (Math.Abs(newZoom - zoomLevel) < 0.0001) return;

            // 3. 调整中心点，使鼠标指向的地理位置在屏幕上位置不变
            double effectiveFactor = newZoom / zoomLevel;
            zoomLevel = newZoom;
            viewCenterX = mouseGeoX - (mouseGeoX - viewCenterX) / effectiveFactor;
            viewCenterY = mouseGeoY - (mouseGeoY - viewCenterY) / effectiveFactor;
        }

        public void ZoomIn() => zoomLevel *= 1.2;

        public void ZoomOut()
        {
            zoomLevel /= 1.2;
            if (zoomLevel < 0.1) zoomLevel = 0.1;
        }

        /// <summary>
        /// 重置视图以显示所有数据
        /// </summary>
        public void ResetView()
        {
            var fullBounds = GetDataBounds();
            viewCenterX = (fullBounds.MinX + fullBounds.MaxX) / 2;
            viewCenterY = (fullBounds.MinY + fullBounds.MaxY) / 2;
            zoomLevel = 1.0;
            isViewInitialized = true;
        }

        #endregion

        #region 3. 空间计算 (Spatial Math)

        /// <summary>
        /// 查找最近的节点（基于 Haversine 距离）
        /// </summary>
        public StreetNode FindNearestNode(double lon, double lat)
        {
            if (Nodes == null || Nodes.Count == 0) return null;

            StreetNode best = null;
            double bestDist = double.MaxValue;

            foreach (var n in Nodes)
            {
                double d = Haversine(lat, lon, n.Lat, n.Lon);
                if (d < bestDist)
                {
                    best = n;
                    bestDist = d;
                }
            }
            return best;
        }

        /// <summary>
        /// 计算两点间的方位角 (0-360度)
        /// </summary>
        public static double CalculateBearing(double lat1, double lon1, double lat2, double lon2)
        {
            var dLon = (lon2 - lon1) * Math.PI / 180.0;
            var y = Math.Sin(dLon) * Math.Cos(lat2 * Math.PI / 180.0);
            var x = Math.Cos(lat1 * Math.PI / 180.0) * Math.Sin(lat2 * Math.PI / 180.0) -
                    Math.Sin(lat1 * Math.PI / 180.0) * Math.Cos(lat2 * Math.PI / 180.0) * Math.Cos(dLon);
            var brng = Math.Atan2(y, x);
            return (brng * 180.0 / Math.PI + 360.0) % 360.0;
        }

        /// <summary>
        /// Haversine 公式计算地球表面两点距离（米）
        /// </summary>
        private static double Haversine(double lat1, double lon1, double lat2, double lon2)
        {
            double R = 6371000; // 地球半径 (米)

            double dLat = (lat2 - lat1) * Math.PI / 180;
            double dLon = (lon2 - lon1) * Math.PI / 180;

            lat1 *= Math.PI / 180;
            lat2 *= Math.PI / 180;

            double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                       Math.Cos(lat1) * Math.Cos(lat2) *
                       Math.Sin(dLon / 2) * Math.Sin(dLon / 2);

            return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        }

        #endregion

        #region 4. 渲染逻辑 (Rendering)

        /// <summary>
        /// 根据当前节点和指定的方位角，寻找该方向上的下一个节点
        /// </summary>
        /// <param name="currentNodeId">当前节点ID</param>
        /// <param name="targetBearing">点击的目标方位角 (0-360度，0为北)</param>
        /// <param name="maxDistance">最大查找距离（米），防止跳到太远的地方</param>
        /// <param name="angleTolerance">角度容差（度），点击方向左右多少度范围内算有效</param>
        /// <returns>找到的节点，如果没有则返回 null</returns>
        public StreetNode FindNextNodeInDirection(int currentNodeId, double targetBearing, double maxDistance = 100, double angleTolerance = 45)
        {
            var currentNode = Nodes.FirstOrDefault(n => n.Id == currentNodeId);
            if (currentNode == null) return null;

            StreetNode bestNode = null;
            double minDistance = double.MaxValue;

            foreach (var node in Nodes)
            {
                // 1. 跳过自身和没有图片的节点
                if (node.Id == currentNode.Id || string.IsNullOrEmpty(node.ImagePath)) continue;

                // 2. 计算距离
                double dist = Haversine(currentNode.Lat, currentNode.Lon, node.Lat, node.Lon);
                if (dist > maxDistance) continue;

                // 3. 计算从当前点到该候选点的真实方位角
                double bearing = CalculateBearing(currentNode.Lat, currentNode.Lon, node.Lat, node.Lon);

                // 4. 计算点击角度与真实角度的偏差 (处理 0/360 度交界处的问题)
                double diff = Math.Abs(bearing - targetBearing);
                if (diff > 180) diff = 360 - diff;

                // 5. 筛选：必须在角度容差范围内，且取距离最近的一个
                if (diff < angleTolerance)
                {
                    // 优化：优先选择距离适中的点，太近（<5米）可能是同一个位置的重复拍摄
                    // 这里简单逻辑：找最近的
                    if (dist < minDistance)
                    {
                        minDistance = dist;
                        bestNode = node;
                    }
                }
            }

            return bestNode;
        }

        /// <summary>
        /// 生成当前地图视图的 Bitmap
        /// </summary>
        /// <param name="activeNodeId">当前高亮的节点ID</param>
        public Bitmap RenderMap(List<StreetNode> nodes, int canvasWidth, int canvasHeight, int activeNodeId = -1)
        {
            try
            {
                var bounds = GetCurrentViewBounds();
                if (canvasWidth <= 0) canvasWidth = 1;
                if (canvasHeight <= 0) canvasHeight = 1;

                var bitmap = new Bitmap(canvasWidth, canvasHeight);
                using (var graphics = Graphics.FromImage(bitmap))
                {
                    graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

                    // 1. 设置背景色
                    graphics.Clear(Color.FromArgb(255, 248, 220)); // 米黄色背景

                    // 2. 绘制网格
                    // [修复] 之前的代码先画网格后 Clear，导致网格不可见。已调整顺序。
                    DrawGrid(graphics, bounds, canvasWidth, canvasHeight);

                    // 3. 绘制路网
                    DrawLines(graphics, Lines, bounds, canvasWidth, canvasHeight);

                    // 4. 绘制节点
                    DrawNodes(graphics, nodes, bounds, canvasWidth, canvasHeight, activeNodeId);
                }
                return bitmap;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[渲染失败] {ex.Message}");
                return new Bitmap(1, 1);
            }
        }

        /// <summary>
        /// 绘制路网（双重描边效果）
        /// </summary>
        private void DrawLines(Graphics graphics, List<StreetLine> lines, MapBounds bounds, int width, int height)
        {
            if (lines == null || lines.Count == 0) return;

            var allScreenLines = new List<DrawingPoint[]>();

            // 1. 将所有地理坐标预转换为屏幕坐标
            foreach (var line in lines)
            {
                if (line.Coordinates == null || line.Coordinates.Length < 2) continue;

                var points = new List<DrawingPoint>();
                foreach (var coord in line.Coordinates)
                {
                    int screenX = (int)((coord.X - bounds.MinX) / bounds.Width * width);
                    // Y轴翻转：屏幕坐标原点在左上，地理坐标Y轴向上
                    int screenY = height - (int)((coord.Y - bounds.MinY) / bounds.Height * height);
                    points.Add(new DrawingPoint(screenX, screenY));
                }
                allScreenLines.Add(points.ToArray());
            }

            // 2. 绘制线条
            // 技巧：先画粗的深色线，再画细的浅色线，形成道路边框效果
            using (var outerPen = new Pen(Color.Gray, 6))
            using (var innerPen = new Pen(Color.White, 4))
            {
                var roundCap = System.Drawing.Drawing2D.LineCap.Round;
                var roundJoin = System.Drawing.Drawing2D.LineJoin.Round;

                outerPen.StartCap = roundCap; outerPen.EndCap = roundCap; outerPen.LineJoin = roundJoin;
                innerPen.StartCap = roundCap; innerPen.EndCap = roundCap; innerPen.LineJoin = roundJoin;

                // 批量绘制
                foreach (var points in allScreenLines)
                {
                    try { graphics.DrawLines(outerPen, points); } catch { }
                }
                foreach (var points in allScreenLines)
                {
                    try { graphics.DrawLines(innerPen, points); } catch { }
                }
            }
        }

        private void DrawGrid(Graphics graphics, MapBounds bounds, int width, int height)
        {
            using (var pen = new Pen(Color.FromArgb(50, Color.DarkGray), 1)) // 半透明网格
            {
                double gridSize = CalculateGridSize(bounds);

                // 对齐网格起始线
                double startX = Math.Floor(bounds.MinX / gridSize) * gridSize;
                double startY = Math.Floor(bounds.MinY / gridSize) * gridSize;

                // 绘制纵向线
                for (double x = startX; x <= bounds.MaxX; x += gridSize)
                {
                    int screenX = (int)((x - bounds.MinX) / bounds.Width * width);
                    graphics.DrawLine(pen, screenX, 0, screenX, height);
                }

                // 绘制横向线
                for (double y = startY; y <= bounds.MaxY; y += gridSize)
                {
                    int screenY = height - (int)((y - bounds.MinY) / bounds.Height * height);
                    graphics.DrawLine(pen, 0, screenY, width, screenY);
                }
            }
        }
        /// <summary>
        /// 绘制节点 (修改版：仅显示有图片的点)
        /// </summary>
        private void DrawNodes(Graphics graphics, List<StreetNode> nodes, MapBounds bounds, int width, int height, int activeNodeId)
        {
            if (nodes == null || nodes.Count == 0) return;

            // 移除不再需要的灰色画笔
            // using (var noImageBrush = new SolidBrush(Color.Gray)) 
            using (var hasImageBrush = new SolidBrush(Color.Red))
            using (var activeBrush = new SolidBrush(Color.Cyan))
            using (var outlinePen = new Pen(Color.Black, 1))
            using (var activeOutlinePen = new Pen(Color.White, 2))
            {
                foreach (var node in nodes)
                {
                    if (string.IsNullOrEmpty(node.ImagePath))
                    {
                        continue;
                    }

                    // 坐标转换
                    float screenX = (float)((node.Lon - bounds.MinX) / bounds.Width * width);
                    float screenY = (float)(height - (node.Lat - bounds.MinY) / bounds.Height * height);

                    // 检查是否是当前选中的高亮节点
                    if (node.Id == activeNodeId)
                    {
                        // 绘制高亮当前点 (靶心效果)
                        float size = 16;
                        graphics.FillEllipse(activeBrush, screenX - size / 2, screenY - size / 2, size, size);
                        graphics.DrawEllipse(activeOutlinePen, screenX - size / 2, screenY - size / 2, size, size);
                        graphics.FillEllipse(Brushes.Black, screenX - 2, screenY - 2, 4, 4);
                    }
                    else
                    {
                        // 绘制普通点 (仅绘制红色的有图点)
                        float size = 6;
                        graphics.FillEllipse(hasImageBrush, screenX - size / 2, screenY - size / 2, size, size);
                        graphics.DrawEllipse(outlinePen, screenX - size / 2, screenY - size / 2, size, size);
                    }
                }
            }
        }

        /// <summary>
        /// 动态计算网格大小，使其在当前视图下保持合适的密度
        /// </summary>
        private double CalculateGridSize(MapBounds bounds)
        {
            double range = Math.Max(bounds.Width, bounds.Height);
            if (range == 0) return 10;
            double baseSize = Math.Pow(10, Math.Floor(Math.Log10(range)));
            if (range / baseSize > 5) return baseSize * 2;
            if (range / baseSize > 2) return baseSize;
            return baseSize / 2;
        }

        #endregion

        #region 5. 辅助类与方法

        /// <summary>
        /// 获取当前视口的地理边界
        /// </summary>
        public MapBounds GetCurrentViewBounds(double screenWidth = 0, double screenHeight = 0)
        {
            var fullBounds = GetDataBounds();
            if (!isViewInitialized) ResetView();

            double currentGeoWidth = fullBounds.Width / zoomLevel;
            double currentGeoHeight;

            // 保持地理纵横比与屏幕一致，防止地图变形
            if (screenWidth > 0 && screenHeight > 0)
            {
                double screenRatio = screenWidth / screenHeight;
                currentGeoHeight = currentGeoWidth / screenRatio;
            }
            else
            {
                currentGeoHeight = fullBounds.Height / zoomLevel;
            }

            return new MapBounds(
                viewCenterX - currentGeoWidth / 2,
                viewCenterY - currentGeoHeight / 2,
                viewCenterX + currentGeoWidth / 2,
                viewCenterY + currentGeoHeight / 2
            );
        }

        /// <summary>
        /// 获取所有数据（点+线）的最大外接矩形
        /// </summary>
        public MapBounds GetDataBounds()
        {
            double minX = double.MaxValue, maxX = double.MinValue;
            double minY = double.MaxValue, maxY = double.MinValue;
            bool hasData = false;

            // 统计点数据
            if (Nodes != null && Nodes.Count > 0)
            {
                minX = Math.Min(minX, Nodes.Min(n => n.Lon));
                maxX = Math.Max(maxX, Nodes.Max(n => n.Lon));
                minY = Math.Min(minY, Nodes.Min(n => n.Lat));
                maxY = Math.Max(maxY, Nodes.Max(n => n.Lat));
                hasData = true;
            }

            // 统计线数据
            if (Lines != null && Lines.Count > 0)
            {
                foreach (var line in Lines)
                {
                    if (line.Coordinates == null) continue;
                    foreach (var coord in line.Coordinates)
                    {
                        if (coord.X < minX) minX = coord.X;
                        if (coord.X > maxX) maxX = coord.X;
                        if (coord.Y < minY) minY = coord.Y;
                        if (coord.Y > maxY) maxY = coord.Y;
                    }
                }
                hasData = true;
            }

            if (!hasData) return new MapBounds(0, 0, 100, 100);

            // 添加 10% 的内边距
            double width = maxX - minX;
            double height = maxY - minY;
            if (width == 0) width = 0.01;
            if (height == 0) height = 0.01;

            double marginX = width * 0.1;
            double marginY = height * 0.1;

            return new MapBounds(minX - marginX, minY - marginY, maxX + marginX, maxY + marginY);
        }

        // 兼容保留旧方法名，指向 GetDataBounds 逻辑
        public MapBounds GetMapBounds() => GetDataBounds();

        /// <summary>
        /// 地图边界类
        /// </summary>
        public class MapBounds
        {
            public double MinX { get; }
            public double MinY { get; }
            public double MaxX { get; }
            public double MaxY { get; }
            public double Width => MaxX - MinX;
            public double Height => MaxY - MinY;

            public MapBounds(double minX, double minY, double maxX, double maxY)
            {
                MinX = minX; MinY = minY; MaxX = maxX; MaxY = maxY;
            }
        }

        /// <summary>
        /// 坐标转换器：处理 屏幕像素 <-> 地理坐标 的相互转换
        /// </summary>
        public class CoordinateTransformer
        {
            private MapBounds bounds;
            private System.Windows.Size canvasSize;

            public CoordinateTransformer(MapBounds mapBounds, System.Windows.Size size)
            {
                bounds = mapBounds;
                canvasSize = size;
            }

            // Geo -> Screen
            public System.Windows.Point GeoToScreen(double x, double y)
            {
                double xRatio = (x - bounds.MinX) / bounds.Width;
                double yRatio = 1.0 - (y - bounds.MinY) / bounds.Height; // Y轴翻转
                return new System.Windows.Point(xRatio * canvasSize.Width, yRatio * canvasSize.Height);
            }

            // Screen -> Geo
            public (double x, double y) ScreenToGeo(System.Windows.Point screenPoint)
            {
                double xRatio = screenPoint.X / canvasSize.Width;
                double yRatio = 1.0 - (screenPoint.Y / canvasSize.Height); // Y轴翻转
                double x = bounds.MinX + xRatio * bounds.Width;
                double y = bounds.MinY + yRatio * bounds.Height;
                return (x, y);
            }
        }

        #endregion
    }
}