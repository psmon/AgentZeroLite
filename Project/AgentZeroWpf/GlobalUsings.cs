// Agent.Common (ZeroCommon) — 공통 모듈 글로벌 임포트
// 기존 AgentZeroWpf.* 경로 대신 Agent.Common.* 경로로 재매핑되어, 이동된 타입을
// 네임스페이스 추가 없이 프로젝트 전체에서 사용 가능하게 한다.
global using Agent.Common;
global using Agent.Common.Actors;
global using Agent.Common.Data;
global using Agent.Common.Data.Entities;
global using Agent.Common.Module;
global using Agent.Common.Services;

global using Application = System.Windows.Application;
global using Brushes = System.Windows.Media.Brushes;
global using Clipboard = System.Windows.Clipboard;
global using Color = System.Windows.Media.Color;
global using ColorConverter = System.Windows.Media.ColorConverter;
global using ContextMenu = System.Windows.Controls.ContextMenu;
global using Cursors = System.Windows.Input.Cursors;
global using FontFamily = System.Windows.Media.FontFamily;
global using KeyEventArgs = System.Windows.Input.KeyEventArgs;
global using MessageBox = System.Windows.MessageBox;
global using Button = System.Windows.Controls.Button;
global using MenuItem = System.Windows.Controls.MenuItem;
global using Orientation = System.Windows.Controls.Orientation;
global using Panel = System.Windows.Controls.Panel;
global using MouseEventArgs = System.Windows.Input.MouseEventArgs;
global using UserControl = System.Windows.Controls.UserControl;
