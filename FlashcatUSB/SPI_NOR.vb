﻿'COPYRIGHT EMBEDDEDCOMPUTERS.NET 2019 - ALL RIGHTS RESERVED
'THIS SOFTWARE IS ONLY FOR USE WITH GENUINE FLASHCATUSB
'CONTACT EMAIL: contact@embeddedcomputers.net
'ANY USE OF THIS CODE MUST ADHERE TO THE LICENSE FILE INCLUDED WITH THIS SDK
'ACKNOWLEDGEMENT: USB driver functionality provided by LibUsbDotNet (sourceforge.net/projects/libusbdotnet) 

Imports FlashcatUSB.FlashMemory
Imports FlashcatUSB.USB
Imports FlashcatUSB.USB.HostClient

Namespace SPI

    Public Class SPI_Programmer : Implements MemoryDeviceUSB
        Private FCUSB As FCUSB_DEVICE
        Public Event PrintConsole(msg As String) Implements MemoryDeviceUSB.PrintConsole
        Public Event SetProgress(percent As Integer) Implements MemoryDeviceUSB.SetProgress
        Public Property MyFlashDevice As SPI_NOR 'Contains the definition of the Flash device that is connected
        Public Property MyFlashStatus As DeviceStatus = DeviceStatus.NotDetected
        Public Property DIE_SELECTED As Integer = 0
        Public Property W25M121AV_Mode As Boolean = False

        Sub New(ByVal parent_if As FCUSB_DEVICE)
            FCUSB = parent_if
        End Sub

        Friend Function DeviceInit() As Boolean Implements MemoryDeviceUSB.DeviceInit
            Me.W25M121AV_Mode = False
            MyFlashStatus = DeviceStatus.NotDetected
            SPIBUS_Setup()
            SPIBUS_WriteRead({&HC2, 0}) 'always select die 0
            Dim ReadSuccess As Boolean = False
            Dim MFG As Byte
            Dim ID1 As UInt16
            Dim ID2 As UInt16
            Dim DEVICEID As SPI_IDENT = ReadDeviceID() 'Sends RDID/REMS/RES command and reads back
            If (DEVICEID.MANU = &HFF Or DEVICEID.MANU = 0) Then 'Check REMS
                If Not ((DEVICEID.REMS = &HFFFF) Or (DEVICEID.REMS = &H0)) Then
                    MFG = (DEVICEID.REMS >> 8)
                    ID1 = (DEVICEID.REMS And 255)
                    ReadSuccess = True 'RDID did not return anything, but REMS did
                End If
            ElseIf (DEVICEID.RDID = &HFFFFFFFFUI) Or (DEVICEID.RDID = 0) Then
            Else
                MFG = DEVICEID.MANU
                ID1 = (DEVICEID.RDID >> 16)
                ID2 = (DEVICEID.RDID And &HFFFF)
                ReadSuccess = True
            End If
            If ReadSuccess Then
                RaiseEvent PrintConsole(RM.GetString("spi_device_opened"))
                Dim RDID_Str As String = "0x" & Hex(DEVICEID.MANU).PadLeft(2, "0") & Hex((DEVICEID.RDID And &HFFFF0000UL) >> 16).PadLeft(4, "0")
                Dim RDID2_Str As String = Hex(DEVICEID.RDID And &HFFFF).PadLeft(4, "0")
                Dim REMS_Str As String = "0x" & Hex(DEVICEID.REMS).PadLeft(4, "0")
                RaiseEvent PrintConsole(String.Format(RM.GetString("spi_connected_to_flash_spi"), RDID_Str, REMS_Str))
                MyFlashDevice = FlashDatabase.FindDevice(MFG, ID1, ID2, MemoryType.SERIAL_NOR, DEVICEID.FMY)
                If MyFlashDevice IsNot Nothing Then
                    MyFlashStatus = DeviceStatus.Supported
                    ResetDevice()
                    LoadDeviceConfigurations() 'Does device settings (4BYTE mode, unlock global block)
                    LoadVendorSpecificConfigurations() 'Some devices may need additional configurations
                    RaiseEvent PrintConsole(String.Format(RM.GetString("flash_detected"), Me.DeviceName, Format(Me.DeviceSize, "#,###")))
                    RaiseEvent PrintConsole(String.Format(RM.GetString("spi_flash_page_size"), Format(MyFlashDevice.ERASE_SIZE, "#,###")))
                    Utilities.Sleep(50)
                    Return True
                Else
                    RaiseEvent PrintConsole(RM.GetString("unknown_device_email"))
                    MyFlashDevice = New SPI_NOR("Unknown", VCC_IF.SPI_3V, 0, DEVICEID.MANU, ID1)
                    MyFlashStatus = DeviceStatus.NotSupported
                    Return False
                End If
            Else
                MyFlashStatus = DeviceStatus.NotDetected
                RaiseEvent PrintConsole(RM.GetString("spi_flash_not_detected"))
                Return False
            End If
        End Function

#Region "Public Interface"

        Friend ReadOnly Property DeviceName() As String Implements MemoryDeviceUSB.DeviceName
            Get
                Select Case MyFlashStatus
                    Case DeviceStatus.Supported
                        Return MyFlashDevice.NAME
                    Case DeviceStatus.NotSupported
                        Return Hex(MyFlashDevice.MFG_CODE).PadLeft(2, CChar("0")) & " " & Hex(MyFlashDevice.ID1).PadLeft(4, CChar("0"))
                    Case Else
                        Return RM.GetString("no_flash_detected")
                End Select
            End Get
        End Property

        Friend ReadOnly Property DeviceSize As Long Implements MemoryDeviceUSB.DeviceSize
            Get
                If Not MyFlashStatus = USB.DeviceStatus.Supported Then Return 0
                Return MyFlashDevice.FLASH_SIZE
            End Get
        End Property

        Friend ReadOnly Property SectorSize(ByVal sector As UInt32, Optional ByVal memory_area As FlashArea = FlashArea.Main) As UInt32 Implements MemoryDeviceUSB.SectorSize
            Get
                If Not MyFlashStatus = USB.DeviceStatus.Supported Then Return 0
                If MyFlashDevice.ERASE_REQUIRED Then
                    Return MyFlashDevice.ERASE_SIZE
                Else
                    Return MyFlashDevice.FLASH_SIZE
                End If
            End Get
        End Property

        Friend Function SectorFind(ByVal sector_index As UInt32, Optional ByVal memory_area As FlashArea = FlashArea.Main) As Long Implements MemoryDeviceUSB.SectorFind
            If sector_index = 0 Then Return 0 'Addresses start at the base address 
            Return Me.SectorSize(0, memory_area) * sector_index
        End Function

        Friend Function SectorCount() As UInt32 Implements MemoryDeviceUSB.SectorCount
            If MyFlashStatus = USB.DeviceStatus.Supported Then
                Dim EraseSize As UInt32 = MyFlashDevice.ERASE_SIZE
                If EraseSize = 0 Then Return 1
                Dim FlashSize As UInt32 = Me.DeviceSize()
                If FlashSize < EraseSize Then Return 1
                Return CInt(FlashSize / EraseSize)
            End If
            Return 0
        End Function

        Friend Function ReadData(ByVal flash_offset As Long, ByVal data_count As UInt32, Optional ByVal memory_area As FlashArea = FlashArea.Main) As Byte() Implements MemoryDeviceUSB.ReadData
            If Me.W25M121AV_Mode Then SPIBUS_WriteRead({&HC2, 0}) : WaitUntilReady()
            Dim data_to_read(data_count - 1) As Byte
            Dim bytes_left As UInt32 = data_count
            Dim buffer_size As UInt32 = 0
            Dim array_ptr As UInt32 = 0
            Dim read_cmd As Byte = MyFlashDevice.OP_COMMANDS.READ
            Dim dummy_clocks As Byte = 0
            If MySettings.SPI_FASTREAD AndAlso FCUSB.HWBOARD = FCUSB_BOARD.Professional_PCB4 Then
                read_cmd = MyFlashDevice.OP_COMMANDS.FAST_READ
            End If
            If (Me.MyFlashDevice.ProgramMode = FlashMemory.SPI_ProgramMode.Atmel45Series) Then
                Dim PageSize As UInt32 = MyFlashDevice.PAGE_SIZE
                If MyFlashDevice.EXTENDED_MODE Then PageSize = MyFlashDevice.PAGE_SIZE_EXTENDED
                Dim AddrOffset As Integer = Math.Ceiling(Math.Log(PageSize, 2)) 'Number of bits the address is offset
                Dim PageAddr As Integer = Math.Floor(flash_offset / PageSize)
                Dim PageOffset As Integer = flash_offset - (PageAddr * PageSize)
                Dim addr_bytes() As Byte = Utilities.Bytes.FromUInt24((PageAddr << AddrOffset) + PageOffset, False)
                Dim at45_addr As UInt32 = (PageAddr << AddrOffset) + PageOffset
                dummy_clocks = (4 * 8) '(4 extra bytes)
                Dim setup_class As New ReadSetupPacket(read_cmd, at45_addr, data_to_read.Length, MyFlashDevice.AddressBytes) With {.DUMMY = dummy_clocks}
                FCUSB.USB_SETUP_BULKIN(USBREQ.SPI_READFLASH, setup_class.ToBytes, data_to_read, 0)
            ElseIf Me.MyFlashDevice.ProgramMode = FlashMemory.SPI_ProgramMode.SPI_EEPROM Then
                If MyFlashDevice.ADDRESSBITS = 8 Then 'Used on ST M95010 - M95040 (8bit) and ATMEL devices (AT25010A - AT25040A)
                    If (flash_offset > 255) Then read_cmd = CByte(read_cmd Or 8) 'Used on M95040 / AT25040A
                    Dim setup_class As New ReadSetupPacket(read_cmd, CUInt(flash_offset And 255), data_to_read.Length, MyFlashDevice.AddressBytes)
                    FCUSB.USB_SETUP_BULKIN(USBREQ.SPI_READFLASH, setup_class.ToBytes, data_to_read, 0)
                Else
                    Dim setup_class As New ReadSetupPacket(read_cmd, flash_offset, data_to_read.Length, MyFlashDevice.AddressBytes) With {.DUMMY = dummy_clocks}
                    FCUSB.USB_SETUP_BULKIN(USBREQ.SPI_READFLASH, setup_class.ToBytes, data_to_read, 0)
                End If
            Else 'Normal SPI READ
                If MySettings.SPI_FASTREAD AndAlso (FCUSB.HWBOARD = FCUSB_BOARD.Professional_PCB4) Then
                    dummy_clocks = MyFlashDevice.SPI_DUMMY
                End If
                If (MyFlashDevice.STACKED_DIES > 1) Then
                    Do Until bytes_left = 0
                        Dim die_address As UInt32 = GetAddressForMultiDie(flash_offset, bytes_left, buffer_size)
                        Dim die_data(buffer_size - 1) As Byte
                        Dim setup_class As New ReadSetupPacket(read_cmd, die_address, die_data.Length, MyFlashDevice.AddressBytes) With {.DUMMY = dummy_clocks}
                        FCUSB.USB_SETUP_BULKIN(USBREQ.SPI_READFLASH, setup_class.ToBytes, die_data, 0)
                        Array.Copy(die_data, 0, data_to_read, array_ptr, die_data.Length) : array_ptr += buffer_size
                    Loop
                Else
                    Dim setup_class As New ReadSetupPacket(read_cmd, flash_offset, data_to_read.Length, MyFlashDevice.AddressBytes) With {.DUMMY = dummy_clocks}
                    FCUSB.USB_SETUP_BULKIN(USBREQ.SPI_READFLASH, setup_class.ToBytes, data_to_read, 0)
                    FCUSB.USB_WaitForComplete()
                End If
            End If
            Return data_to_read
        End Function

        Friend Function WriteData(ByVal flash_offset As Long, ByVal data_to_write() As Byte, Optional ByRef Params As WriteParameters = Nothing) As Boolean Implements MemoryDeviceUSB.WriteData
            If Me.W25M121AV_Mode Then SPIBUS_WriteRead({&HC2, 0}) : WaitUntilReady()
            Dim bytes_left As UInt32 = data_to_write.Length
            Dim buffer_size As UInt32 = 0
            Dim array_ptr As UInt32 = 0
            Select Case MyFlashDevice.ProgramMode
                Case SPI_ProgramMode.PageMode
                    If (MyFlashDevice.STACKED_DIES > 1) Then 'Multi-die support
                        Dim write_result As Boolean
                        Do Until bytes_left = 0
                            Dim die_address As UInt32 = GetAddressForMultiDie(flash_offset, bytes_left, buffer_size)
                            Dim die_data(buffer_size - 1) As Byte
                            Array.Copy(data_to_write, array_ptr, die_data, 0, die_data.Length) : array_ptr += buffer_size
                            Dim setup_packet As New WriteSetupPacket(MyFlashDevice, die_address, die_data.Length)
                            write_result = WriteData_Flash(setup_packet, die_data)
                            If Not write_result Then Return False
                        Loop
                        Return write_result
                    Else
                        Dim setup_packet As New WriteSetupPacket(MyFlashDevice, flash_offset, data_to_write.Length)
                        Dim result As Boolean = WriteData_Flash(setup_packet, data_to_write)
                        Return result
                    End If
                Case SPI_ProgramMode.SPI_EEPROM 'Used on most ST M95080 and above
                    Return WriteData_SPI_EEPROM(flash_offset, data_to_write)
                Case SPI_ProgramMode.AAI_Byte
                    Return WriteData_AAI(flash_offset, data_to_write, False)
                Case SPI_ProgramMode.AAI_Word
                    Return WriteData_AAI(flash_offset, data_to_write, True)
                Case SPI_ProgramMode.Atmel45Series
                    Return WriteData_AT45(flash_offset, data_to_write)
                Case SPI_ProgramMode.Nordic
                    Dim data_left As Integer = data_to_write.Length
                    Dim ptr As Integer = 0
                    Do While (data_left > 0)
                        SPIBUS_WriteEnable()
                        Dim packet_data(MyFlashDevice.PAGE_SIZE - 1) As Byte
                        Array.Copy(data_to_write, ptr, packet_data, 0, packet_data.Length)
                        Dim setup_packet As New WriteSetupPacket(MyFlashDevice, flash_offset + ptr, packet_data.Length)
                        Dim write_result As Boolean = WriteData_Flash(setup_packet, packet_data)
                        If Not write_result Then Return False
                        WaitUntilReady()
                        ptr += packet_data.Length
                        data_left -= packet_data.Length
                    Loop
                    Return True
            End Select
            Return False
        End Function

        Friend Function SectorErase(ByVal sector_index As UInt32, Optional ByVal memory_area As FlashArea = FlashArea.Main) As Boolean Implements MemoryDeviceUSB.SectorErase
            If Me.W25M121AV_Mode Then SPIBUS_WriteRead({&HC2, 0}) : WaitUntilReady()
            If (Not MyFlashDevice.ERASE_REQUIRED) Then Return True 'Erase not needed
            Dim flash_offset As UInt32 = Me.SectorFind(sector_index, memory_area)
            If MyFlashDevice.ProgramMode = SPI_ProgramMode.Atmel45Series Then
                Dim PageSize As UInt32 = MyFlashDevice.PAGE_SIZE
                If MyFlashDevice.EXTENDED_MODE Then PageSize = MyFlashDevice.PAGE_SIZE_EXTENDED
                Dim EraseSize As UInt32 = MyFlashDevice.ERASE_SIZE
                Dim AddrOffset As UInt32 = Math.Ceiling(Math.Log(PageSize, 2)) 'Number of bits the address is offset
                Dim blocknum As UInt32 = Math.Floor(flash_offset / EraseSize)
                Dim addrbytes() As Byte = Utilities.Bytes.FromUInt24(blocknum << (AddrOffset + 3), False)
                SPIBUS_WriteRead({MyFlashDevice.OP_COMMANDS.SE, addrbytes(0), addrbytes(1), addrbytes(2)}, Nothing)
            ElseIf MyFlashDevice.ProgramMode = SPI_ProgramMode.Nordic Then
                SPIBUS_WriteEnable() ': Utilities.Sleep(50)
                Dim PageNum As Byte = Math.Floor(flash_offset / MyFlashDevice.ERASE_SIZE)
                SPIBUS_WriteRead({MyFlashDevice.OP_COMMANDS.SE, PageNum}, Nothing)
            Else
                SPIBUS_WriteEnable()
                If (MyFlashDevice.STACKED_DIES > 1) Then 'Multi-die support
                    flash_offset = GetAddressForMultiDie(flash_offset, 0, 0)
                End If
                Dim DataToWrite() As Byte = GetArrayWithCmdAndAddr(MyFlashDevice.OP_COMMANDS.SE, flash_offset)
                SPIBUS_WriteRead(DataToWrite, Nothing)
                If MyFlashDevice.SEND_RDFS Then
                    ReadFlagStatusRegister()
                Else
                    Utilities.Sleep(10)
                End If
            End If
            WaitUntilReady()
            Return True
        End Function

        Friend Function SectorWrite(ByVal sector_index As UInt32, ByVal data() As Byte, Optional ByRef Params As WriteParameters = Nothing) As Boolean Implements MemoryDeviceUSB.SectorWrite
            If Me.W25M121AV_Mode Then SPIBUS_WriteRead({&HC2, 0}) : WaitUntilReady()
            Dim Addr32 As UInteger = Me.SectorFind(sector_index, Params.Memory_Area)
            Return WriteData(Addr32, data, Params)
        End Function

        Friend Function EraseDevice() As Boolean Implements MemoryDeviceUSB.EraseDevice
            If Me.W25M121AV_Mode Then SPIBUS_WriteRead({&HC2, 0}) : WaitUntilReady()
            If MyFlashDevice.ProgramMode = FlashMemory.SPI_ProgramMode.Atmel45Series Then
                SPIBUS_WriteRead({&HC7, &H94, &H80, &H9A}, Nothing)
            ElseIf MyFlashDevice.ProgramMode = SPI_ProgramMode.SPI_EEPROM Then
                Dim data(MyFlashDevice.FLASH_SIZE - 1) As Byte
                Utilities.FillByteArray(data, 255)
                WriteData(0, data)
            ElseIf MyFlashDevice.ProgramMode = SPI_ProgramMode.Nordic Then
                'This device does support chip-erase, but it will also erase the InfoPage content
                Dim nord_timer As New Stopwatch : nord_timer.Start()
                RaiseEvent PrintConsole(String.Format(RM.GetString("spi_erasing_flash_device"), Format(Me.DeviceSize, "#,###")))
                Dim TotalPages As Integer = MyFlashDevice.FLASH_SIZE / MyFlashDevice.PAGE_SIZE
                For i = 0 To TotalPages - 1
                    SPIBUS_WriteEnable() : Utilities.Sleep(50)
                    SPIBUS_WriteRead({MyFlashDevice.OP_COMMANDS.SE, i}, Nothing)
                    WaitUntilReady()
                Next
                RaiseEvent PrintConsole(String.Format(RM.GetString("spi_erase_complete"), Format(nord_timer.ElapsedMilliseconds / 1000, "#.##")))
                Return True
            Else
                RaiseEvent PrintConsole(String.Format(RM.GetString("spi_erasing_flash_device"), Format(Me.DeviceSize, "#,###")))
                Dim erase_timer As New Stopwatch : erase_timer.Start()
                Select Case MyFlashDevice.CHIP_ERASE
                    Case EraseMethod.Standard
                        SPIBUS_WriteEnable()
                        SPIBUS_WriteRead({MyFlashDevice.OP_COMMANDS.BE}, Nothing) '&HC7
                        If MyFlashDevice.SEND_RDFS Then ReadFlagStatusRegister()
                        WaitUntilReady()
                    Case EraseMethod.BySector
                        Dim SectorCount As UInt32 = MyFlashDevice.Sector_Count
                        RaiseEvent SetProgress(0)
                        For i As UInt32 = 0 To SectorCount - 1
                            If (Not SectorErase(i, FlashArea.NotSpecified)) Then
                                RaiseEvent SetProgress(0) : Return False 'Error erasing sector
                            Else
                                Dim progress As Single = CSng((i / SectorCount) * 100)
                                RaiseEvent SetProgress(Math.Floor(progress))
                            End If
                        Next
                        RaiseEvent SetProgress(0) 'Device successfully erased
                    Case EraseMethod.DieErase
                        EraseDie()
                    Case EraseMethod.Micron
                        Dim internal_timer As New Stopwatch
                        internal_timer.Start()
                        SPIBUS_WriteEnable() 'Try Chip Erase first
                        SPIBUS_WriteRead({MyFlashDevice.OP_COMMANDS.BE}, Nothing)
                        If MyFlashDevice.SEND_RDFS Then ReadFlagStatusRegister()
                        WaitUntilReady()
                        internal_timer.Stop()
                        If (internal_timer.ElapsedMilliseconds < 1000) Then 'Command not supported, use DIE ERASE instead
                            EraseDie()
                        End If
                End Select
                RaiseEvent PrintConsole(String.Format(RM.GetString("spi_erase_complete"), Format(erase_timer.ElapsedMilliseconds / 1000, "#.##")))
            End If
            Return True
        End Function
        'Reads the SPI status register and waits for the device to complete its current operation
        Friend Sub WaitUntilReady() Implements MemoryDeviceUSB.WaitUntilReady
            Try
                Dim Status As UInt32
                If MyFlashDevice.ProgramMode = SPI_ProgramMode.Atmel45Series Then
                    Do
                        Dim sr() As Byte = ReadStatusRegister()
                        Status = sr(0)
                        If Not ((Status And &H80) > 0) Then Utilities.Sleep(50)
                    Loop While Not ((Status And &H80) > 0)
                Else
                    Do
                        Dim sr() As Byte = ReadStatusRegister()
                        Status = sr(0)
                        If AppIsClosing Then Exit Sub
                        If Status = 255 Then Exit Do
                        If (Status And 1) Then Utilities.Sleep(5)
                    Loop While (Status And 1)
                    If MyFlashDevice IsNot Nothing AndAlso MyFlashDevice.ProgramMode = SPI_ProgramMode.Nordic Then
                        Utilities.Sleep(50)
                    End If
                End If
            Catch ex As Exception
            End Try
        End Sub

#End Region

        Private Sub LoadDeviceConfigurations()
            If MyFlashDevice.VENDOR_SPECIFIC = VENDOR_FEATURE.NotSupported Then 'We don't want to do this for vendor enabled devices
                WriteStatusRegister({0})
                Utilities.Sleep(100) 'Needed, some devices will lock up if else.
            End If
            If (MyFlashDevice.STACKED_DIES > 1) Then 'Multi-die support
                For i = 0 To MyFlashDevice.STACKED_DIES - 1
                    SPIBUS_WriteRead({MyFlashDevice.OP_COMMANDS.DIESEL, i}) : WaitUntilReady() 'We need to make sure DIE 0 is selected
                    If MyFlashDevice.SEND_EN4B Then
                        SPIBUS_SendCommand(MyFlashDevice.OP_COMMANDS.EN4B) 'Set options for each DIE
                    End If
                    SPIBUS_SendCommand(MyFlashDevice.OP_COMMANDS.ULBPR) '0x98 (global block unprotect)
                Next
                SPIBUS_WriteRead({MyFlashDevice.OP_COMMANDS.DIESEL, 0}) : WaitUntilReady() 'We need to make sure DIE 0 is selected
                Me.DIE_SELECTED = 0
            Else
                If MyFlashDevice.SEND_EN4B Then SPIBUS_SendCommand(MyFlashDevice.OP_COMMANDS.EN4B) '0xB7
                SPIBUS_SendCommand(MyFlashDevice.OP_COMMANDS.ULBPR) '0x98 (global block unprotect)
            End If
        End Sub

        Private Function ReadDeviceID() As SPI_IDENT
            Dim DEVICEID As New SPI_IDENT
            Dim rdid(5) As Byte
            SPIBUS_WriteRead({SPI_Command_DEF.RDID}, rdid) 'This reads SPI CHIP ID
            Dim rems(1) As Byte
            SPIBUS_WriteRead({SPI_Command_DEF.REMS, 0, 0, 0}, rems) 'Some devices (such as SST25VF512) only support REMS
            Dim res(0) As Byte
            SPIBUS_WriteRead({SPI_Command_DEF.RES, 0, 0, 0}, res)
            DEVICEID.MANU = rdid(0)
            DEVICEID.RDID = (CUInt(rdid(1)) << 24) Or (CUInt(rdid(2)) << 16) Or (CUInt(rdid(3)) << 8) Or CUInt(rdid(4))
            DEVICEID.FMY = rdid(5)
            DEVICEID.REMS = (CUShort(rems(0)) << 8) Or rems(1)
            DEVICEID.RES = res(0)
            Return DEVICEID
        End Function
        'This writes to the SR (multi-bytes can be input to write as well)
        Public Function WriteStatusRegister(ByVal NewValues() As Byte) As Boolean
            Try
                If NewValues Is Nothing Then Return False
                SPIBUS_WriteEnable() 'Some devices such as AT25DF641 require the WREN and the status reg cleared before we can write data
                If MyFlashDevice.SEND_EWSR Then
                    SPIBUS_WriteRead({MyFlashDevice.OP_COMMANDS.EWSR}, Nothing) 'Send the command that we are going to enable-write to register
                    Threading.Thread.Sleep(20) 'Wait a brief moment
                End If
                Dim cmd(NewValues.Length) As Byte
                cmd(0) = MyFlashDevice.OP_COMMANDS.WRSR
                Array.Copy(NewValues, 0, cmd, 1, NewValues.Length)
                If Not SPIBUS_WriteRead(cmd, Nothing) = cmd.Length Then Return False
                Return True
            Catch ex As Exception
                Return False
            End Try
        End Function

        Public Function ReadStatusRegister(Optional Count As Integer = 1) As Byte()
            Try
                Dim Output(Count - 1) As Byte
                SPIBUS_WriteRead({MyFlashDevice.OP_COMMANDS.RDSR}, Output)
                Return Output
            Catch ex As Exception
                Return Nothing 'Erorr
            End Try
        End Function

        Private Function GetArrayWithCmdAndAddr(ByVal cmd As Byte, ByVal addr_offset As UInt32) As Byte()
            Dim addr_data() As Byte = BitConverter.GetBytes(addr_offset)
            ReDim Preserve addr_data(MyFlashDevice.AddressBytes - 1)
            Array.Reverse(addr_data)
            Dim data_out(MyFlashDevice.AddressBytes) As Byte
            data_out(0) = cmd
            For i = 1 To data_out.Length - 1
                data_out(i) = addr_data(i - 1)
            Next
            Return data_out
        End Function

        Private Sub ReadFlagStatusRegister()
            Utilities.Sleep(10)
            Dim flag() As Byte = {0}
            Do
                SPIBUS_WriteRead({MyFlashDevice.OP_COMMANDS.RDFR}, flag)
            Loop Until ((flag(0) >> 7) And 1)
        End Sub

        Private Sub EraseDie()
            Dim die_size As UInt32 = &H2000000
            Dim die_count As UInt32 = MyFlashDevice.FLASH_SIZE / die_size
            For x As UInt32 = 1 To die_count
                RaiseEvent PrintConsole(String.Format(RM.GetString("spi_erasing_die"), x.ToString, Format(die_size, "#,###")))
                Dim die_addr() As Byte = Utilities.Bytes.FromUInt32((x - 1) * die_size, False)
                SPIBUS_WriteEnable()
                SPIBUS_WriteRead({MyFlashDevice.OP_COMMANDS.DE, die_addr(0), die_addr(1), die_addr(1), die_addr(1)}, Nothing) '&HC4
                Utilities.Sleep(1000)
                If MyFlashDevice.SEND_RDFS Then ReadFlagStatusRegister()
                WaitUntilReady()
            Next
        End Sub

        Public Sub ResetDevice()
            SPIBUS_WriteRead({&HF0}) 'SPI RESET COMMAND
            'Other commands: 0x66 and 0x99
            Utilities.Sleep(10)
        End Sub

#Region "Vendor Specific"

        Private Sub LoadVendorSpecificConfigurations()
            If (MyFlashDevice.ProgramMode = SPI_ProgramMode.Atmel45Series) Then 'May need to load the current page mode
                Dim sr() As Byte = ReadStatusRegister() 'Some devices have 2 SR
                Dim page_size As UInt32 = MyFlashDevice.PAGE_SIZE
                MyFlashDevice.EXTENDED_MODE = False
                If (sr(0) And 1) = 0 Then 'Device uses extended pages
                    MyFlashDevice.EXTENDED_MODE = True
                    page_size = MyFlashDevice.PAGE_SIZE_EXTENDED
                    MyFlashDevice.FLASH_SIZE = MyFlashDevice.PAGE_COUNT * MyFlashDevice.PAGE_SIZE_EXTENDED
                    MyFlashDevice.ERASE_SIZE = (MyFlashDevice.PAGE_SIZE_EXTENDED * 8) 'Block erase = 8 pages
                End If
                RaiseEvent PrintConsole("Device configured to page size: " & page_size & " bytes")
            End If
            If (MyFlashDevice.MFG_CODE = &HBF) Then 'SST26VF016/SST26VF032 requires block protection to be removed in SQI only
                If MyFlashDevice.ID1 = &H2601 Or MyFlashDevice.ID1 = &H2602 Then
                    RaiseEvent PrintConsole("SQI mode must be used to remove block protection")
                End If
            ElseIf (MyFlashDevice.MFG_CODE = &H9D) Then 'ISSI
                WriteStatusRegister({0}) 'Erase protection bits
            End If
            If (MyFlashDevice.MFG_CODE = &HEF) AndAlso (MyFlashDevice.ID1 = &H4018) Then
                SPIBUS_WriteRead({&HC2, 1}) : WaitUntilReady() 'Check to see if this device has two dies
                Dim id As SPI_IDENT = ReadDeviceID()
                If (id.RDID = &HEFAB2100UI) Then Me.W25M121AV_Mode = True
            End If
            If (MyFlashDevice.MFG_CODE = &H34) Then 'Cypress MFG ID
                Dim IsCypressSemper As Boolean = False
                If MyFlashDevice.ID1 = &H2A19 Then IsCypressSemper = True
                If MyFlashDevice.ID1 = &H2A1A Then IsCypressSemper = True
                If MyFlashDevice.ID1 = &H2A1B Then IsCypressSemper = True
                If MyFlashDevice.ID1 = &H2B19 Then IsCypressSemper = True
                If MyFlashDevice.ID1 = &H2B1A Then IsCypressSemper = True
                If MyFlashDevice.ID1 = &H2B1B Then IsCypressSemper = True
                If IsCypressSemper Then
                    RaiseEvent PrintConsole("Configuring Cypress Semper Flash via SPI")
                    SPIBUS_WriteEnable()
                    SPIBUS_WriteRead({MyFlashDevice.OP_COMMANDS.EWSR})
                    SPIBUS_WriteRead({MyFlashDevice.OP_COMMANDS.WRSR, 0, 0, &H88, &H18}) 'Set sector to uniform 256KB / 512B Page size
                End If
            End If
        End Sub
        'Changes the Page Size configuration bit in nonvol
        Public Function Atmel_SetPageConfiguration(ByVal EnableExtPage As Boolean) As Boolean
            If (Not EnableExtPage) Then
                Dim ReadBack As Integer = SPIBUS_WriteRead({&H3D, &H2A, &H80, &HA6}, Nothing)
                WaitUntilReady()
                If ReadBack = 4 Then Return True
            Else
                Dim ReadBack As Integer = SPIBUS_WriteRead({&H3D, &H2A, &H80, &HA7}, Nothing)
                WaitUntilReady()
                If ReadBack = 4 Then Return True
            End If
            Return False
        End Function

#End Region

#Region "Programming Algorithums"
        'Returns the die address from the flash_offset (and increases by the buffersize) and also selects the correct die
        Private Function GetAddressForMultiDie(ByRef flash_offset As UInt32, ByRef count As UInt32, ByRef buffer_size As UInt32) As UInt32
            Dim die_size As UInt32 = (MyFlashDevice.FLASH_SIZE / MyFlashDevice.STACKED_DIES)
            Dim die_id As Byte = CByte(Math.Floor(flash_offset / die_size))
            Dim die_addr As UInt32 = (flash_offset Mod die_size)
            If (MyFlashDevice.MFG_CODE = &H20) Then 'Micron uses a different die system
                buffer_size = Math.Min(count, ((MyFlashDevice.FLASH_SIZE / MyFlashDevice.STACKED_DIES) - die_addr))
                count -= buffer_size
                die_addr = flash_offset
                flash_offset += buffer_size
            Else
                buffer_size = Math.Min(count, ((MyFlashDevice.FLASH_SIZE / MyFlashDevice.STACKED_DIES) - die_addr))
                If (die_id <> Me.DIE_SELECTED) Then
                    SPIBUS_WriteRead({MyFlashDevice.OP_COMMANDS.DIESEL, die_id})
                    WaitUntilReady()
                    Me.DIE_SELECTED = die_id
                End If
                count -= buffer_size
                flash_offset += buffer_size
            End If
            Return die_addr
        End Function

        Private Function WriteData_Flash(ByVal setup_packet As WriteSetupPacket, ByVal data_out() As Byte) As Boolean
            Try
                Dim result As Boolean
                If (FCUSB.IsProfessional) Then
                    Dim DataToWrite As UInt32 = data_out.Length
                    Dim PacketSize As UInt32 = 8192
                    Dim Loops As Integer = CInt(Math.Ceiling(DataToWrite / PacketSize)) 'Calcuates iterations
                    For i As Integer = 0 To Loops - 1
                        Dim BufferSize As Integer = DataToWrite
                        If (BufferSize > PacketSize) Then BufferSize = PacketSize
                        Dim data(BufferSize - 1) As Byte
                        setup_packet.WR_COUNT = BufferSize
                        Array.Copy(data_out, (i * PacketSize), data, 0, data.Length)
                        result = FCUSB.USB_SETUP_BULKOUT(USBREQ.SPI_WRITEFLASH, setup_packet.ToBytes, data, 0, 1000)
                        If Not result Then Return False
                        Utilities.Sleep(5)
                        setup_packet.DATA_OFFSET += data.Length
                        DataToWrite -= data.Length
                        result = FCUSB.USB_WaitForComplete()
                    Next
                Else
                    result = FCUSB.USB_SETUP_BULKOUT(USBREQ.SPI_WRITEFLASH, setup_packet.ToBytes, data_out, 0)
                End If
                Utilities.Sleep(6) 'Needed
                Return result
            Catch ex As Exception
                Return False
            End Try
        End Function
        'Designed for SPI EEPROMS where each page needs to wait until ready
        Private Function WriteData_SPI_EEPROM(ByVal offset As UInt32, ByVal data_to_write() As Byte) As Boolean
            Dim PageSize As UInt32 = MyFlashDevice.PAGE_SIZE
            Dim DataToWrite As Integer = data_to_write.Length
            For i As Integer = 0 To Math.Ceiling(DataToWrite / PageSize) - 1
                Dim BufferSize As Integer = DataToWrite
                If (BufferSize > PageSize) Then BufferSize = PageSize
                Dim data(BufferSize - 1) As Byte
                Array.Copy(data_to_write, (i * PageSize), data, 0, data.Length)
                Dim addr_size As Integer = (MyFlashDevice.ADDRESSBITS / 8)
                Dim packet(data.Length + addr_size) As Byte 'OPCMD,ADDR,DATA
                packet(0) = MyFlashDevice.OP_COMMANDS.PROG 'First byte is the write command
                If (addr_size = 1) Then
                    Dim addr8 As Byte
                    If (offset > 255) Then
                        packet(0) = CByte(MyFlashDevice.OP_COMMANDS.PROG Or 8) 'Enables 4th bit
                        addr8 = CByte(offset And 255) 'Lower 8 bits only
                    Else
                        packet(0) = CByte(MyFlashDevice.OP_COMMANDS.PROG And &HF7) 'Disables 4th bit
                        addr8 = CByte(offset)
                    End If
                    packet(1) = addr8
                    Array.Copy(data, 0, packet, 2, data.Length)
                ElseIf (addr_size = 2) Then
                    packet(1) = CByte((offset >> 8) And 255)
                    packet(2) = CByte(offset And 255)
                    Array.Copy(data, 0, packet, 3, data.Length)
                ElseIf (addr_size = 3) Then
                    packet(1) = CByte((offset >> 16) And 255)
                    packet(2) = CByte((offset >> 8) And 255)
                    packet(3) = CByte(offset And 255)
                    Array.Copy(data, 0, packet, 4, data.Length)
                End If
                SPIBUS_WriteEnable()
                SPIBUS_SlaveSelect_Enable()
                SPIBUS_WriteData(packet)
                SPIBUS_SlaveSelect_Disable()
                Utilities.Sleep(10)
                WaitUntilReady()
                offset += data.Length
                DataToWrite -= data.Length
            Next
            Return True
        End Function

        Private Function WriteData_AAI(ByVal flash_offset As UInteger, ByVal data() As Byte, ByVal word_mode As Boolean) As Boolean
            If word_mode AndAlso (Not data.Length Mod 2 = 0) Then 'We must write a even number of bytes
                ReDim Preserve data(data.Length)
                data(data.Length - 1) = 255 'Fill the last byte with 0xFF
            End If
            SPIBUS_WriteEnable()
            MyFlashDevice.PAGE_SIZE = 1024 'No page size needed when doing AAI
            Dim setup_packet As New WriteSetupPacket(MyFlashDevice, flash_offset, data.Length)
            If word_mode Then
                setup_packet.CMD_PROG = MyFlashDevice.OP_COMMANDS.AAI_WORD
            Else
                setup_packet.CMD_PROG = MyFlashDevice.OP_COMMANDS.AAI_BYTE
            End If
            Dim ctrl As UInt32 = (Utilities.BoolToInt(word_mode) + 1)
            Dim Result As Boolean = FCUSB.USB_SETUP_BULKOUT(USBREQ.SPI_WRITEDATA_AAI, setup_packet.ToBytes, data, ctrl)
            If Not Result Then Return False
            Utilities.Sleep(6) 'Needed for some reason
            FCUSB.USB_WaitForComplete()
            SPIBUS_WriteDisable()
            Return True
        End Function
        'Uses an internal sram buffer to transfer data from the board to the flash (used by Atmel AT45DBxxx)
        Private Function WriteData_AT45(ByVal offset As UInteger, ByVal DataOut() As Byte) As Boolean
            Try
                Dim data_size As UInt32 = DataOut.Length
                Dim PageSize As UInt32 = MyFlashDevice.PAGE_SIZE
                If MyFlashDevice.EXTENDED_MODE Then PageSize = MyFlashDevice.PAGE_SIZE_EXTENDED
                Dim AddrOffset As Integer = Math.Ceiling(Math.Log(PageSize, 2)) 'Number of bits the address is offset
                Dim BytesLeft As UInt32 = DataOut.Length
                Do Until BytesLeft = 0
                    Dim BytesToWrite As Integer = BytesLeft
                    If BytesToWrite > PageSize Then BytesToWrite = PageSize
                    Dim DataToBuffer(BytesToWrite + 3) As Byte
                    DataToBuffer(0) = MyFlashDevice.OP_COMMANDS.WRTB '0x84
                    Dim src_ind As Integer = DataOut.Length - BytesLeft
                    Array.Copy(DataOut, src_ind, DataToBuffer, 4, BytesToWrite)
                    SPIBUS_SlaveSelect_Enable()
                    SPIBUS_WriteData(DataToBuffer)
                    SPIBUS_SlaveSelect_Disable()
                    WaitUntilReady()
                    Dim PageAddr As Integer = Math.Floor(offset / PageSize)
                    Dim PageCmd() As Byte = Utilities.Bytes.FromUInt24(PageAddr << AddrOffset, False)
                    Dim Cmd2() As Byte = {MyFlashDevice.OP_COMMANDS.WRFB, PageCmd(0), PageCmd(1), PageCmd(2)} '0x88
                    SPIBUS_WriteRead(Cmd2, Nothing)
                    WaitUntilReady()
                    offset += BytesToWrite
                    BytesLeft -= BytesToWrite
                Loop
                Return True
            Catch ex As Exception
            End Try
            Return False
        End Function

#End Region

#Region "SPIBUS"

        Public Sub SPIBUS_Setup()
            Me.FCUSB.USB_SPI_SETUP(MySettings.SPI_MODE, MySettings.SPI_BIT_ORDER)
            Utilities.Sleep(50) 'Allow time for device to change IO
        End Sub

        Public Function SPIBUS_WriteEnable() As Boolean
            If SPIBUS_WriteRead({MyFlashDevice.OP_COMMANDS.WREN}, Nothing) = 1 Then
                Return True
            Else
                Return False
            End If
        End Function

        Public Function SPIBUS_WriteDisable() As Boolean
            If SPIBUS_WriteRead({MyFlashDevice.OP_COMMANDS.WRDI}, Nothing) = 1 Then
                Return True
            Else
                Return False
            End If
        End Function

        Public Function SPIBUS_SendCommand(ByVal spi_cmd As Byte) As Boolean
            Dim we As Boolean = SPIBUS_WriteEnable()
            If Not we Then Return False
            Return SPIBUS_WriteRead({spi_cmd}, Nothing)
        End Function

        Public Function SPIBUS_WriteRead(ByVal WriteBuffer() As Byte, Optional ByRef ReadBuffer() As Byte = Nothing) As UInt32
            If WriteBuffer Is Nothing And ReadBuffer Is Nothing Then Return 0
            Dim TotalBytesTransfered As UInt32 = 0
            SPIBUS_SlaveSelect_Enable()
            If (WriteBuffer IsNot Nothing) Then
                Dim BytesWritten As Integer = 0
                Dim Result As Boolean = SPIBUS_WriteData(WriteBuffer)
                If WriteBuffer.Length > 2048 Then Utilities.Sleep(2) 'Needed for PRO
                If Result Then TotalBytesTransfered += WriteBuffer.Length
            End If
            If (ReadBuffer IsNot Nothing) Then
                Dim BytesRead As Integer = 0
                Dim Result As Boolean = SPIBUS_ReadData(ReadBuffer)
                If Result Then TotalBytesTransfered += ReadBuffer.Length
            End If
            SPIBUS_SlaveSelect_Disable()
            Return TotalBytesTransfered
        End Function
        'Makes the CS/SS pin go low
        Private Sub SPIBUS_SlaveSelect_Enable()
            Try
                FCUSB.USB_CONTROL_MSG_OUT(USBREQ.SPI_SS_ENABLE)
                Utilities.Sleep(1)
            Catch ex As Exception
            End Try
        End Sub
        'Releases the CS/SS pin
        Private Sub SPIBUS_SlaveSelect_Disable()
            Try
                FCUSB.USB_CONTROL_MSG_OUT(USBREQ.SPI_SS_DISABLE)
                Utilities.Sleep(1)
            Catch ex As Exception
            End Try
        End Sub

        Private Function SPIBUS_WriteData(ByVal DataOut() As Byte) As Boolean
            Dim Success As Boolean
            Try
                Success = FCUSB.USB_SETUP_BULKOUT(USBREQ.SPI_WR_DATA, Nothing, DataOut, DataOut.Length)
                Utilities.Sleep(2)
            Catch ex As Exception
                Return False
            End Try
            If Not Success Then RaiseEvent PrintConsole(RM.GetString("spi_error_writing"))
            Return True
        End Function

        Private Function SPIBUS_ReadData(ByRef Data_In() As Byte) As Boolean
            Dim Success As Boolean = False
            Try
                Success = FCUSB.USB_SETUP_BULKIN(USBREQ.SPI_RD_DATA, Nothing, Data_In, Data_In.Length)
            Catch ex As Exception
            End Try
            If Not Success Then RaiseEvent PrintConsole(RM.GetString("spi_error_reading"))
            Return Success
        End Function

        Public Sub SetProgPin(ByVal enabled As Boolean)
            Try
                Dim value As UInt32 = 0 'Set to LOW
                If enabled Then value = 1 'Set to HIGH
                FCUSB.USB_CONTROL_MSG_OUT(USBREQ.SPI_PROG, Nothing, value)
            Catch ex As Exception
            End Try
        End Sub

#End Region

    End Class

    Public Enum SPI_ORDER As Integer
        SPI_ORDER_MSB_FIRST = 0
        SPI_ORDER_LSB_FIRST = 1
    End Enum

    Public Enum SPI_CLOCK_POLARITY As Integer
        SPI_MODE_0 = 0 'CPOL(0),CPHA(0),CKE(1)
        SPI_MODE_1 = 1 'CPOL(0),CPHA(1),CKE(0)
        SPI_MODE_2 = 2 'CPOL(1),CPHA(0),CKE(1)
        SPI_MODE_3 = 3 'CPOL(1),CPHA(1),CKE(0)
    End Enum

    Public Enum SQI_SPEED As Integer
        MHZ_80
        MHZ_40
        MHZ_20
        MHZ_10
        MHZ_5
        MHZ_2
        MHZ_1
    End Enum

    Friend Class ReadSetupPacket
        Property READ_CMD As Byte
        Property DATA_OFFSET As UInt32
        Property COUNT As UInt32
        Property ADDR_BYTES As Byte 'Number of bytes used for the read command (MyFlashDevice.AddressBytes)
        Property DUMMY As Integer = 0 'Number of clock toggles before reading data
        Property SPI_MODE As SPI_QUAD_SUPPORT = SPI_QUAD_SUPPORT.NO_QUAD

        Sub New(ByVal cmd As Byte, ByVal offset As UInt32, ByVal d_count As UInt32, ByVal addr_size As Byte)
            Me.READ_CMD = cmd
            Me.DATA_OFFSET = offset
            Me.COUNT = d_count
            Me.ADDR_BYTES = addr_size
        End Sub

        Public Function ToBytes() As Byte()
            Dim setup_data(10) As Byte '12 bytes
            setup_data(0) = READ_CMD 'READ/FAST_READ/ETC.
            setup_data(1) = CByte(Me.ADDR_BYTES)
            setup_data(2) = CByte((Me.DATA_OFFSET And &HFF000000) >> 24)
            setup_data(3) = CByte((Me.DATA_OFFSET And &HFF0000) >> 16)
            setup_data(4) = CByte((Me.DATA_OFFSET And &HFF00) >> 8)
            setup_data(5) = CByte(Me.DATA_OFFSET And &HFF)
            setup_data(6) = CByte((COUNT And &HFF0000) >> 16)
            setup_data(7) = CByte((COUNT And &HFF00) >> 8)
            setup_data(8) = CByte(COUNT And &HFF)
            setup_data(9) = DUMMY 'Number of dummy bytes
            setup_data(10) = Me.SPI_MODE
            Return setup_data
        End Function

    End Class

    Friend Class WriteSetupPacket
        Public Property CMD_PROG As Byte
        Public Property CMD_WREN As Byte
        Public Property CMD_RDSR As Byte
        Public Property CMD_RDFR As Byte
        Public Property CMD_WR As Byte
        Public Property PAGE_SIZE As UInt32
        Public Property SEND_RDFS As Boolean = False
        Property DATA_OFFSET As UInt32
        Property WR_COUNT As UInt32
        Property ADDR_BYTES As Byte 'Number of bytes used for the read command 
        Property SPI_MODE As SPI_QUAD_SUPPORT = SPI_QUAD_SUPPORT.NO_QUAD

        Sub New(ByVal spi_dev As SPI_NOR, ByVal offset As UInt32, ByVal d_count As UInt32)
            Me.CMD_PROG = spi_dev.OP_COMMANDS.PROG
            Me.CMD_WREN = spi_dev.OP_COMMANDS.WREN
            Me.CMD_RDSR = spi_dev.OP_COMMANDS.RDSR
            Me.CMD_RDFR = spi_dev.OP_COMMANDS.RDFR 'Flag Status Register
            Me.ADDR_BYTES = spi_dev.AddressBytes
            Me.DATA_OFFSET = offset
            Me.WR_COUNT = d_count
            Me.SEND_RDFS = spi_dev.SEND_RDFS
            Me.PAGE_SIZE = spi_dev.PAGE_SIZE
        End Sub

        Public Function ToBytes() As Byte()
            Dim setup_data(15) As Byte '16 bytes
            setup_data(0) = Me.CMD_PROG
            setup_data(1) = Me.CMD_WREN
            setup_data(2) = Me.CMD_RDSR
            setup_data(3) = Me.CMD_RDFR
            setup_data(4) = CByte(Me.ADDR_BYTES) 'Number of bytes to write
            setup_data(5) = CByte((Me.PAGE_SIZE And &HFF00) >> 8)
            setup_data(6) = CByte(Me.PAGE_SIZE And &HFF)
            setup_data(7) = CByte((Me.DATA_OFFSET And &HFF000000) >> 24)
            setup_data(8) = CByte((Me.DATA_OFFSET And &HFF0000) >> 16)
            setup_data(9) = CByte((Me.DATA_OFFSET And &HFF00) >> 8)
            setup_data(10) = CByte(Me.DATA_OFFSET And &HFF)
            setup_data(11) = CByte((Me.WR_COUNT And &HFF0000) >> 16)
            setup_data(12) = CByte((Me.WR_COUNT And &HFF00) >> 8)
            setup_data(13) = CByte(Me.WR_COUNT And &HFF)
            setup_data(14) = Me.SPI_MODE
            If (Not Me.SEND_RDFS) Then setup_data(3) = 0 'Only use flag-reg if required
            Return setup_data
        End Function

    End Class

    Friend Class SPI_IDENT
        Public MANU As Byte '(MFG)
        Public RDID As UInt32 '(ID1,ID2,ID3,ID4)
        Public FMY As Byte '(ID5)
        Public REMS As UInt16 '(MFG)(ID1)
        Public RES As Byte '(MFG)

        Sub New()

        End Sub

        Public ReadOnly Property DETECTED As Boolean
            Get
                If Me.MANU = 0 OrElse Me.MANU = &HFF Then Return False
                If Me.RDID = 0 OrElse Me.RDID = &HFFFF Then Return False
                If Me.MANU = CByte(Me.RDID And 255) And Me.MANU = CByte((Me.RDID >> 8) And 255) Then Return False
                Return True
            End Get
        End Property

    End Class

    Public Enum SPIBUS_MODE As Byte
        SPI_MODE_0 = 1
        SPI_MODE_1 = 2
        SPI_MODE_2 = 3
        SPI_MODE_3 = 4
        SPI_UNSPECIFIED
    End Enum

End Namespace
