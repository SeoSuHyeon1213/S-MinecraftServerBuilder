using Builder.Gui;

ApplicationConfiguration.Initialize();
// 첫 인자로 서버 폴더를 받으면 그 폴더를 연다 (예: mchb-gui servers\my-server).
Application.Run(new MainForm(args.FirstOrDefault()));
