using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using HelixToolkit.Wpf;
using System.Drawing;
using Point = System.Windows.Point;
using HelixToolkit.Geometry;
using System.Windows.Controls;
using Microsoft.Win32;
using static LocalStreetViewApp.MapManager;

namespace LocalStreetViewApp
{
    public partial class MainWindow : Window
    {
        private double rotationY = 0.0;
        private double rotationX = 0.0;
        private bool isDragging = false;
        private Point lastMousePos;

        private PerspectiveCamera camera;
        private ModelVisual3D sphereModelVisual;
        private GeometryModel3D sphereGeometry;
        private bool isMapPanning = false;
        private Point mapPanStartPoint;

        // 使用 StreetNode 替代 PanoramaLocation
        private List<StreetNode> streetNodes = new List<StreetNode>();
        private int currentIndex = 0;

        // 地图相关变量
        private MapManager mapManager;
        private CoordinateTransformer coordinateTransformer;

        public MainWindow()
        {
            InitializeComponent();
            SetupViewport();
            InitializeMap();
            AttachMouseHandlers();
            this.KeyDown += MainWindow_KeyDown;
            MapImage.SizeChanged += (s, e) => UpdateMapDisplay();
        }
        private void MapImage_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            isMapPanning = true;
            mapPanStartPoint = e.GetPosition(MapImage);
            // 捕获鼠标，防止拖出图片区域后松开鼠标导致状态未重置
            MapImage.CaptureMouse();

            // 改变鼠标样式提示用户
            MapImage.Cursor = Cursors.SizeAll;
        }
        private void InitializeMap()
        {
            try
            {
                mapManager = new MapManager();
                // 初始渲染地图
                UpdateMapDisplay();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"地图初始化失败: {ex.Message}");
            }
        }

        private void UpdateMapDisplay()
        {
            try
            {
                if (mapManager == null) return;

                // 获取 MapImage 控件的实际像素大小，如果还未渲染则给个默认值
                int w = (int)MapImage.ActualWidth;
                int h = (int)MapImage.ActualHeight;
                if (w <= 0 || h <= 0) { w = 800; h = 300; }

                // 1. 传入控件大小进行渲染
                using (var mapBitmap = mapManager.RenderMap(streetNodes, w, h))
                {
                    MapImage.Source = BitmapToImageSource(mapBitmap);
                }

                var currentBounds = mapManager.GetCurrentViewBounds();
                coordinateTransformer = new CoordinateTransformer(currentBounds, new System.Windows.Size(w, h));

                // 3. 如果有选中的点，刷新红点位置
                UpdatePositionMarker();
            }
            catch (Exception ex)
            {

            }
        }

        private BitmapImage BitmapToImageSource(Bitmap bitmap)
        {
            using (var memory = new MemoryStream())
            {
                bitmap.Save(memory, System.Drawing.Imaging.ImageFormat.Png);
                memory.Position = 0;

                var bitmapImage = new BitmapImage();
                bitmapImage.BeginInit();
                bitmapImage.StreamSource = memory;
                bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                bitmapImage.EndInit();

                return bitmapImage;
            }
        }
        private void BtnLoadLine_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                OpenFileDialog dlg = new OpenFileDialog();
                dlg.Filter = "Shapefile (*.shp)|*.shp";
                dlg.Title = "选择路网(线) Shapefile";
                // 这里可以设置你的默认路径
                dlg.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                //dlg.InitialDirectory = @"C:\Users\MECHREVO\Desktop\A";
                if (dlg.ShowDialog() == true)
                {
                    // 1. 调用 MapManager 加载线数据
                    mapManager.LoadLineShapefile(dlg.FileName);

                    // 2. 重新渲染地图（RenderMap 现在会自动画出线和点）
                    UpdateMapDisplay();

                    // 3. 重新计算坐标转换器（因为地图范围可能因为加了线而变大）
                    var mapBounds = mapManager.GetMapBounds();
                    coordinateTransformer = new CoordinateTransformer(mapBounds,
                        new System.Windows.Size(MapImage.ActualWidth, MapImage.ActualHeight));

                    // 4. 如果当前有选中的全景点，更新它的红点标记位置
                    UpdatePositionMarker();

                    MessageBox.Show($"路网加载成功，共 {mapManager.Lines.Count} 条道路");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"路网加载失败: {ex.Message}");
            }
        }
        // 加载SHP文件并绑定图片
        private void BtnLoadSHP_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                OpenFileDialog dlg = new OpenFileDialog();
                dlg.Filter = "Shapefile (*.shp)|*.shp";
                dlg.InitialDirectory = @"C:\Users\MECHREVO\Desktop\A";

                if (dlg.ShowDialog() == true)
                {
                    // 加载SHP文件
                    mapManager.LoadShapefile(dlg.FileName);
                    streetNodes = mapManager.Nodes;

                    // 自动绑定图片
                    string imageFolder = @"C:\Users\MECHREVO\Desktop\images";

                    // 如果指定路径不存在，尝试相对路径
                    if (!Directory.Exists(imageFolder))
                    {
                        imageFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data");
                        Console.WriteLine($"使用相对图片路径: {imageFolder}");
                    }

                    int boundCount = mapManager.AutoBindImages(imageFolder);

                    // 更新地图显示
                    UpdateMapDisplay();

                    // 初始化坐标转换器
                    var mapBounds = mapManager.GetMapBounds();
                    coordinateTransformer = new CoordinateTransformer(mapBounds,
                        new System.Windows.Size(MapImage.ActualWidth, MapImage.ActualHeight));

                    string message = $"读取完毕，共 {streetNodes.Count} 个点";
                    if (boundCount > 0)
                    {
                        message += $"，成功绑定 {boundCount} 张图片";

                        // 如果有绑定图片的节点，加载第一个
                        var firstNodeWithImage = streetNodes.FirstOrDefault(n => !string.IsNullOrEmpty(n.ImagePath) && File.Exists(n.ImagePath));
                        if (firstNodeWithImage != null)
                        {
                            currentIndex = streetNodes.IndexOf(firstNodeWithImage);
                            LoadPanorama(currentIndex);
                        }
                    }
                    else
                    {
                        message += "，但没有成功绑定任何图片";
                    }
                    MessageBox.Show(message);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"加载SHP文件失败: {ex.Message}");
                Console.WriteLine($"异常详情: {ex.StackTrace}");
            }
        }

        private System.Windows.Media.Media3D.MeshGeometry3D CreateSphereMesh(double radius, int thetaDiv, int phiDiv)
        {
            var mesh = new System.Windows.Media.Media3D.MeshGeometry3D();

            double dt = Math.PI / thetaDiv;
            double dp = 2 * Math.PI / phiDiv;

            // 创建顶点
            for (int pi = 0; pi <= thetaDiv; pi++)
            {
                double theta = pi * dt;
                for (int ti = 0; ti <= phiDiv; ti++)
                {
                    double phi = ti * dp;

                    double x = radius * Math.Sin(theta) * Math.Cos(phi);
                    double y = radius * Math.Cos(theta);
                    double z = radius * Math.Sin(theta) * Math.Sin(phi);

                    mesh.Positions.Add(new System.Windows.Media.Media3D.Point3D(x, y, z));
                    mesh.TextureCoordinates.Add(new System.Windows.Point(ti / (double)phiDiv, pi / (double)thetaDiv));
                    mesh.Normals.Add(new System.Windows.Media.Media3D.Vector3D(x, y, z));
                }
            }

            // 创建三角形
            for (int pi = 0; pi < thetaDiv; pi++)
            {
                for (int ti = 0; ti < phiDiv; ti++)
                {
                    int x0 = ti;
                    int x1 = (ti + 1);
                    int y0 = pi * (phiDiv + 1);
                    int y1 = (pi + 1) * (phiDiv + 1);

                    mesh.TriangleIndices.Add(y0 + x0);
                    mesh.TriangleIndices.Add(y1 + x0);
                    mesh.TriangleIndices.Add(y0 + x1);

                    mesh.TriangleIndices.Add(y1 + x0);
                    mesh.TriangleIndices.Add(y1 + x1);
                    mesh.TriangleIndices.Add(y0 + x1);
                }
            }

            return mesh;
        }

        private void LoadPanorama(int index)
        {
            if (streetNodes.Count == 0 || index < 0 || index >= streetNodes.Count)
            {
                Console.WriteLine($"无法加载全景: 索引 {index} 超出范围或节点列表为空");
                return;
            }

            var node = streetNodes[index];
            Console.WriteLine($"尝试加载全景: 节点 {index}, 图片路径: {node.ImagePath}");

            if (string.IsNullOrEmpty(node.ImagePath))
            {
                MessageBox.Show("该节点没有绑定的图片文件");
                Console.WriteLine($"节点 {index} 没有图片路径");
                return;
            }

            if (!File.Exists(node.ImagePath))
            {
                MessageBox.Show("图片文件不存在: " + node.ImagePath);
                Console.WriteLine($"图片文件不存在: {node.ImagePath}");
                return;
            }

            currentIndex = index;

            try
            {
                // 读取图片
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(node.ImagePath, UriKind.Absolute);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();

                var imgBrush = new ImageBrush(bmp)
                {
                    Stretch = Stretch.Fill,
                    ViewportUnits = BrushMappingMode.Absolute
                };
                RenderOptions.SetBitmapScalingMode(imgBrush, BitmapScalingMode.HighQuality);

                var material = new EmissiveMaterial(imgBrush);

                // 使用完全限定的类型名称
                var mesh = CreateSphereMesh(10, 64, 32);

                sphereGeometry = new System.Windows.Media.Media3D.GeometryModel3D
                {
                    Geometry = mesh,
                    Material = material,
                    BackMaterial = material
                };

                UpdateSphereRotation();
                sphereModelVisual.Content = sphereGeometry;

                string fileName = Path.GetFileName(node.ImagePath);
                txtInfo.Text = $"{currentIndex + 1}/{streetNodes.Count} - {fileName} ({bmp.PixelWidth}×{bmp.PixelHeight}) - 距离: {node.DistanceToImage:F2}米";

                // 更新地图位置标记
                UpdatePositionMarker();

                Console.WriteLine($"成功加载全景图片: {fileName}");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"加载图片失败: {ex.Message}");
                Console.WriteLine($"加载图片异常: {ex.Message}");
                Console.WriteLine($"堆栈跟踪: {ex.StackTrace}");
            }
        }

        private void MapImage_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (streetNodes.Count == 0 || coordinateTransformer == null) return;

            var clickPoint = e.GetPosition(MapImage);

            // 获取点击位置的地理坐标
            var (x, y) = coordinateTransformer.ScreenToGeo(clickPoint);

            // 查找最近的有图片的节点
            var nearestNode = FindNearestNodeWithImage(x, y);

            if (nearestNode != null)
            {
                // 切换到该全景
                currentIndex = streetNodes.IndexOf(nearestNode);
                LoadPanorama(currentIndex);
            }
        }

        private StreetNode FindNearestNodeWithImage(double lon, double lat)
        {
            return streetNodes
                .Where(n => !string.IsNullOrEmpty(n.ImagePath) && File.Exists(n.ImagePath))
                .OrderBy(n => CalculateDistance(n.Lon, n.Lat, lon, lat))
                .FirstOrDefault();
        }

        private double CalculateDistance(double x1, double y1, double x2, double y2)
        {
            return Math.Sqrt(Math.Pow(x2 - x1, 2) + Math.Pow(y2 - y1, 2));
        }

        private void MapImage_MouseMove(object sender, MouseEventArgs e)
        {
            // 1. 处理拖拽逻辑
            if (isMapPanning && mapManager != null)
            {
                var currentPoint = e.GetPosition(MapImage);

                // 计算位移
                double dx = currentPoint.X - mapPanStartPoint.X;
                double dy = currentPoint.Y - mapPanStartPoint.Y;

                // 如果位移太小（防抖），不处理
                if (Math.Abs(dx) > 1 || Math.Abs(dy) > 1)
                {
                    // 调用 MapManager 进行平移
                    mapManager.Pan(dx, dy, MapImage.ActualWidth, MapImage.ActualHeight);

                    // 重新渲染地图
                    UpdateMapDisplay();

                    // 更新起始点，以便下一次计算增量
                    mapPanStartPoint = currentPoint;
                }
            }

            // 2. 原有的显示坐标逻辑
            if (coordinateTransformer != null)
            {
                var mousePoint = e.GetPosition(MapImage);
                var (x, y) = coordinateTransformer.ScreenToGeo(mousePoint);
                MapCoordText.Text = $"坐标: ({x:F6}, {y:F6})";
            }
        }

        // 更新位置标记
        private void MapImage_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (isMapPanning)
            {
                isMapPanning = false;
                MapImage.ReleaseMouseCapture();
                MapImage.Cursor = Cursors.Arrow; // 恢复鼠标样式
            }
        }
        private void UpdatePositionMarker()
        {
            if (currentIndex < streetNodes.Count && coordinateTransformer != null)
            {
                var node = streetNodes[currentIndex];
                var screenPos = coordinateTransformer.GeoToScreen(node.Lon, node.Lat);

                Canvas.SetLeft(PositionMarker, screenPos.X - 6);
                Canvas.SetTop(PositionMarker, screenPos.Y - 6);
                Canvas.SetLeft(PositionMarkerInner, screenPos.X - 2);
                Canvas.SetTop(PositionMarkerInner, screenPos.Y - 2);

                PositionMarker.Visibility = Visibility.Visible;
                PositionMarkerInner.Visibility = Visibility.Visible;
            }
        }

        // 地图缩放控制
        private void ZoomIn_Click(object sender, RoutedEventArgs e)
        {
            mapManager.ZoomIn();
            UpdateMapDisplay();
            UpdatePositionMarker();
        }

        private void ZoomOut_Click(object sender, RoutedEventArgs e)
        {
            mapManager.ZoomOut();
            UpdateMapDisplay();
            UpdatePositionMarker();
        }

        private void ResetView_Click(object sender, RoutedEventArgs e)
        {
            mapManager.ResetView();
            UpdateMapDisplay();
            UpdatePositionMarker();
        }

        // 全景浏览控制
        private void BtnPrev_Click(object sender, RoutedEventArgs e)
        {
            if (streetNodes.Count == 0) return;

            // 查找前一个有图片的节点
            int newIndex = currentIndex;
            for (int i = 1; i <= streetNodes.Count; i++)
            {
                newIndex = (currentIndex - i + streetNodes.Count) % streetNodes.Count;
                if (!string.IsNullOrEmpty(streetNodes[newIndex].ImagePath) && File.Exists(streetNodes[newIndex].ImagePath))
                    break;
            }

            if (newIndex != currentIndex)
            {
                LoadPanorama(newIndex);
            }
        }

        private void BtnNext_Click(object sender, RoutedEventArgs e)
        {
            if (streetNodes.Count == 0) return;

            // 查找后一个有图片的节点
            int newIndex = currentIndex;
            for (int i = 1; i <= streetNodes.Count; i++)
            {
                newIndex = (currentIndex + i) % streetNodes.Count;
                if (!string.IsNullOrEmpty(streetNodes[newIndex].ImagePath) && File.Exists(streetNodes[newIndex].ImagePath))
                    break;
            }

            if (newIndex != currentIndex)
            {
                LoadPanorama(newIndex);
            }
        }

        // 原有的3D视图方法保持不变
        private void SetupViewport()
        {
            camera = new PerspectiveCamera
            {
                FieldOfView = 70,
                Position = new Point3D(0, 0, 0.001),
                LookDirection = new Vector3D(0, 0, 1),
                UpDirection = new Vector3D(0, 1, 0)
            };
            helixViewport.Camera = camera;

            helixViewport.Children.Add(new DefaultLights());
            sphereModelVisual = new ModelVisual3D();
            helixViewport.Children.Add(sphereModelVisual);
        }

        private void UpdateSphereRotation()
        {
            if (sphereGeometry != null)
            {
                var transformGroup = new Transform3DGroup();
                transformGroup.Children.Add(new RotateTransform3D(
                    new AxisAngleRotation3D(new Vector3D(1, 0, 0), rotationX)));
                transformGroup.Children.Add(new RotateTransform3D(
                    new AxisAngleRotation3D(new Vector3D(0, 1, 0), rotationY)));

                sphereGeometry.Transform = transformGroup;
            }
        }

        private void AttachMouseHandlers()
        {
            helixViewport.MouseDown += HelixViewport_MouseDown;
            helixViewport.MouseMove += HelixViewport_MouseMove;
            helixViewport.MouseUp += HelixViewport_MouseUp;
            helixViewport.MouseWheel += HelixViewport_MouseWheel;
        }

        private void HelixViewport_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                isDragging = true;
                lastMousePos = e.GetPosition(this);
                helixViewport.CaptureMouse();
                Mouse.OverrideCursor = Cursors.SizeAll;
            }
        }

        private void HelixViewport_MouseMove(object sender, MouseEventArgs e)
        {
            if (!isDragging) return;
            var pos = e.GetPosition(this);
            double dx = pos.X - lastMousePos.X;
            double dy = pos.Y - lastMousePos.Y;
            lastMousePos = pos;

            double rotationSpeed = 0.2;
            rotationY = (rotationY + dx * rotationSpeed) % 360;
            rotationX += dy * rotationSpeed * 0.5;

            rotationX = Math.Max(-90, Math.Min(90, rotationX));
            UpdateSphereRotation();
        }

        private void HelixViewport_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                isDragging = false;
                helixViewport.ReleaseMouseCapture();
                Mouse.OverrideCursor = null;
            }
        }

        private void HelixViewport_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            double delta = e.Delta > 0 ? -2 : 2;
            camera.FieldOfView = Math.Max(20, Math.Min(100, camera.FieldOfView + delta));
        }

        private void MainWindow_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Left) BtnPrev_Click(null, null);
            if (e.Key == Key.Right) BtnNext_Click(null, null);
        }
    }

    // 街景节点结构
    //public class StreetNode
    //{
    //    public int Id;
    //    public double Lon;
    //    public double Lat;
    //    public string ImagePath;        // 绑定的街景图片路径
    //    public double DistanceToImage;  // 图片到节点距离
    //}
}