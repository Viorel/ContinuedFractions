﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Windows.Threading;
using ContinuedFractionsLibrary;

namespace ContinuedFractions
{
    /// <summary>
    /// Interaction logic for UCFromContinuedFraction.xaml
    /// </summary>
    public partial class UCFromContinuedFraction : UserControl
    {
        const int MAX_CONTINUED_FRACTION_ITEMS = 100;
        const int MAX_BIGINTEGER_BYTE_SIZE = 128;
        readonly TimeSpan DELAY_BEFORE_CALCULATION = TimeSpan.FromMilliseconds( 444 );
        readonly TimeSpan DELAY_BEFORE_PROGRESS = TimeSpan.FromMilliseconds( 455 ); // (must be greater than 'DELAY_BEFORE_CALCULATION')
        readonly TimeSpan MIN_DURATION_PROGRESS = TimeSpan.FromMilliseconds( 444 );

        bool mLoaded = false;
        bool mIsRestoreError = false;
        readonly DispatcherTimer mCalculationTimer;
        Thread? mCalculationThread = null;
        SimpleCancellable? mLastCancellable = null;
        readonly DispatcherTimer mProgressTimer = new( );
        DateTime mProgressShownTime = DateTime.MinValue;
        enum ProgressStatusEnum { None, DelayToShow, DelayToHide };
        ProgressStatusEnum mProgressStatus = ProgressStatusEnum.None;

        public UCFromContinuedFraction( )
        {
            InitializeComponent( );

            richTextBoxNote.Visibility = Visibility.Visible;
            richTextBoxTypicalError.Visibility = Visibility.Hidden;
            richTextBoxError.Visibility = Visibility.Hidden;
            richTextBoxResults.Visibility = Visibility.Hidden;
            labelPleaseWait.Visibility = Visibility.Hidden;

            mCalculationTimer = new DispatcherTimer
            {
                Interval = DELAY_BEFORE_CALCULATION,
            };
            mCalculationTimer.Tick += CalculationTimer_Tick;

            mProgressTimer.Tick += ProgressTimer_Tick;
        }

        private void UserControl_Loaded( object sender, RoutedEventArgs e )
        {
            // it seems to be called multiple times

            if( !mLoaded )
            {
                mLoaded = true;

                ApplySavedData( );
            }
        }

        private void textBoxContinuedFraction_TextChanged( object sender, TextChangedEventArgs e )
        {
            if( !mLoaded ) return;

            RestartCalculationTimer( );
        }

        private void textBoxContinuedFraction_SelectionChanged( object sender, RoutedEventArgs e )
        {
            if( !mLoaded ) return;

            PostponeCalculationTimer( );
        }

        private void CalculationTimer_Tick( object? sender, EventArgs e )
        {
            mCalculationTimer.Stop( );

            RestartCalculations( );
        }

        void RestartCalculationTimer( )
        {
            mCalculationTimer.Stop( );
            mCalculationTimer.Start( );
            ShowProgress( );
        }

        void PostponeCalculationTimer( )
        {
            if( mCalculationTimer.IsEnabled ) RestartCalculationTimer( );
        }

        void ApplySavedData( )
        {
            try
            {
                textBoxContinuedFraction.Text = Properties.Settings.Default.LastContinuedFraction;

                textBoxContinuedFraction.Focus( );
                textBoxContinuedFraction.SelectAll( );
            }
            catch( Exception exc )
            {
                mIsRestoreError = true;

                if( Debugger.IsAttached ) Debugger.Break( );
                else Debug.Fail( exc.Message, exc.ToString( ) );

                // ignore
            }
        }

        internal void SaveData( )
        {
            if( !mIsRestoreError ) // avoid overwriting details in case of errors
            {
                Properties.Settings.Default.LastContinuedFraction = textBoxContinuedFraction.Text;

                Properties.Settings.Default.Save( );
            }
        }

        internal void Stop( )
        {
            mCalculationTimer.Stop( );
            StopThread( );
        }

        void StopThread( )
        {
            try
            {
                if( mCalculationThread != null )
                {
                    mLastCancellable?.SetCancel( );
                    mCalculationThread.Interrupt( );
                    mCalculationThread.Join( 99 );
                    mCalculationThread = null;
                }
            }
            catch( Exception exc )
            {
                if( Debugger.IsAttached ) Debugger.Break( );
                else Debug.Fail( exc.Message, exc.ToString( ) );

                // ignore?
            }
        }

        void RestartCalculations( )
        {
            try
            {
                StopThread( );

                IReadOnlyList<BigInteger>? continued_fraction = GetInputContinuedFraction( );
                if( continued_fraction == null ) return;

                mLastCancellable = new SimpleCancellable( );
                mCalculationThread = new Thread( ( ) =>
                {
                    CalculationThreadProc( mLastCancellable, continued_fraction );
                } )
                {
                    IsBackground = true,
                    Priority = ThreadPriority.BelowNormal
                };

                mCalculationThread.Start( );
            }
            catch( Exception exc )
            {
                if( Debugger.IsAttached ) Debugger.Break( );

                string error_text = $"Something went wrong.\r\n\r\n{exc.Message}";
                if( Debugger.IsAttached ) error_text = $"{error_text}\r\n{exc.StackTrace}";

                ShowError( error_text );
            }
        }

        IReadOnlyList<BigInteger>? GetInputContinuedFraction( )
        {
            string input_text = textBoxContinuedFraction.Text;

            if( string.IsNullOrWhiteSpace( input_text ) )
            {
                ShowOneRichTextBox( richTextBoxNote );
                HideProgress( );

                return null;
            }

            Match m = RegexToParseContinuedFraction( ).Match( input_text );

            if( !m.Success )
            {
                ShowOneRichTextBox( richTextBoxTypicalError );
                HideProgress( );

                return null;
            }

            BigInteger first = BigInteger.Parse( m.Groups["first"].Value );

            List<BigInteger> list = [first];

            Group next_group = m.Groups["next"];

            if( next_group.Success )
            {
                foreach( Capture c in next_group.Captures )
                {
                    BigInteger item = BigInteger.Parse( c.Value );

                    list.Add( item );
                }
            }

            return list;
        }

        void CalculationThreadProc( ICancellable cnc, IReadOnlyList<BigInteger> continuedFraction )
        {
            try
            {
                CalculationContext ctx = new( cnc, 33 );

                Fraction[] convergents =
                    ContinuedFractionUtilities
                        .EnumerateContinuedFractionConvergents( continuedFraction )
                        .Select( p =>
                                    p.d.IsZero ? p.n < 0 ? Fraction.NegativeInfinity : p.n > 0 ? Fraction.PositiveInfinity : Fraction.Undefined
                                    : new Fraction( p.d < 0 ? -p.n : p.n, BigInteger.Abs( p.d ) )
                                )
                        .ToArray( );

                IReadOnlyList<BigInteger>? corrected_regular_continued_fraction = null;

                Fraction result = convergents.Last( );
                result = result.Simplify( ctx );

                if( result.IsNormal )
                {
                    Debug.Assert( result.E.IsZero );

                    BigInteger[] continued_fraction_items =
                        [.. ContinuedFractionUtilities
                            .EnumerateContinuedFraction( BigInteger.Abs(result.N), result.D )
                            .Take( MAX_CONTINUED_FRACTION_ITEMS + 1 )];

                    if( continued_fraction_items.Length < MAX_CONTINUED_FRACTION_ITEMS )
                    {
                        if( result.IsNegative ) continued_fraction_items = ContinuedFractionUtilities.Negate( continued_fraction_items );

                        bool is_not_regular = !continued_fraction_items.SequenceEqual( continuedFraction );

                        if( is_not_regular )
                        {
                            corrected_regular_continued_fraction = continued_fraction_items;
                        }
                    }
                }

                string? error_text = null;

                if( error_text == null )
                {
                    ShowResults( cnc, result, convergents, corrected_regular_continued_fraction );

                    HideProgress( );
                }

                if( !string.IsNullOrEmpty( error_text ) )
                {
                    ShowError( error_text );
                }
            }
            catch( OperationCanceledException ) // also 'TaskCanceledException'
            {
                // (the operation is supposed to be restarted)
                return;
            }
            catch( Exception exc )
            {
                if( Debugger.IsAttached ) Debugger.Break( );

                string error_text = $"Something went wrong.\r\n\r\n{exc.Message}";
                if( Debugger.IsAttached ) error_text = $"{error_text}\r\n{exc.StackTrace}";

                ShowError( error_text );
            }
        }

        void ShowResults( ICancellable cnc, Fraction result, Fraction[] convergents, IReadOnlyList<BigInteger>? correctedRegularContinuedFraction )
        {
            bool is_corrected = correctedRegularContinuedFraction != null;

            string result_as_decimal = result.ToFloatString( cnc, 20 );

            bool is_negative = result.IsNegative;
            BigInteger n = BigInteger.Abs( result.N );
            BigInteger d = result.D;
            BigInteger e = result.E;

            while( e < 0 )
            {
                d *= 10;
                ++e;

                if( d.GetByteCount( ) > MAX_BIGINTEGER_BYTE_SIZE ) throw new ApplicationException( "The number exceeds the supported limits." );
            }

            while( e > 0 )
            {
                n *= 10;
                --e;

                if( n.GetByteCount( ) > MAX_BIGINTEGER_BYTE_SIZE ) throw new ApplicationException( "The number exceeds the supported limits." );
            }

            string result_as_fraction;

            if( !result.IsNormal )
            {
                result_as_fraction = result.ToRationalString( cnc, 20 ); // 
            }
            else
            {
                result_as_fraction = $"{( is_negative ? -n : n ):D}";
                if( !e.IsZero ) result_as_fraction = $"{result_as_fraction}e{( e >= 0 ? "+" : "" )}{e:D}";
                if( !d.IsOne ) result_as_fraction = $"{result_as_fraction} / {d:D}";
            }

            StringBuilder sb_convergents = new( );
            string convergents_title;

            int convergent_number = 0;
            foreach( Fraction f in convergents )
            {
                sb_convergents
                    .Append( $"{convergent_number.ToString( ).PadLeft( 2, '\u2007' )}:\u2007" );

                if( !f.IsNormal )
                {
                    sb_convergents
                        .AppendLine( f.ToRationalString( cnc, 20 ) );
                }
                else
                {
                    Debug.Assert( f.E == 0 );

                    string fs = f.ToFloatString( cnc, 20 );
                    bool fsa = fs.Contains( '≈' );
                    fs = fs.Replace( "≈", "" );
                    sb_convergents
                        .AppendLine( $"{f.N:D} / {f.D:D} {( fsa ? '≈' : '=' )} {fs}" );
                }

                ++convergent_number;
            }

            StringBuilder sb_corrected = new( );

            if( is_corrected )
            {
                convergents_title = "Convergents of entered continued fraction";

                sb_corrected
                    .Append( "[ " )
                    .Append( correctedRegularContinuedFraction[0].ToString( "D" ) );

                for( int i = 1; i < correctedRegularContinuedFraction.Count; i++ )
                {
                    var item = correctedRegularContinuedFraction[i];

                    sb_corrected
                        .Append( i == 1 ? "; " : ", " )
                        .Append( item.ToString( "D" ) );
                }

                sb_corrected.Append( " ]" );
            }
            else
            {
                convergents_title = "Convergents";
            }

            Dispatcher.BeginInvoke( ( ) =>
                {
                    runDecimal.Text = result_as_decimal;
                    runFraction.Text = result_as_fraction;
                    runCorrected.Text = sb_corrected.ToString( );
                    runConvergents.Text = sb_convergents.ToString( );
                    runConvergentsTitle.Text = convergents_title;

                    UIUtilities.ShowTopBlock( richTextBoxResults.Document, sectionInfo, !is_corrected, sectionFraction );
                    UIUtilities.ShowTopBlock( richTextBoxResults.Document, sectionWarning, is_corrected, sectionFraction );
                    UIUtilities.ShowTopBlock( richTextBoxResults.Document, sectionCorrected, is_corrected, sectionFraction, sectionWarning );

                    ShowOneRichTextBox( richTextBoxResults );
                } );
        }

        void ShowError( string errorText )
        {
            if( !Dispatcher.CheckAccess( ) )
            {
                Dispatcher.BeginInvoke( ( ) =>
                {
                    ShowError( errorText );
                } );
            }
            else
            {
                runError.Text = errorText;
                ShowOneRichTextBox( richTextBoxError );
                HideProgress( );
            }
        }

        void ShowOneRichTextBox( RichTextBox richTextBox )
        {
            bool was_visible = richTextBox.Visibility == Visibility.Visible;

            richTextBoxNote.Visibility = Visibility.Hidden;
            richTextBoxTypicalError.Visibility = Visibility.Hidden;
            richTextBoxError.Visibility = Visibility.Hidden;
            richTextBoxResults.Visibility = Visibility.Hidden;

            if( !was_visible ) richTextBox.ScrollToHome( );
            richTextBox.Visibility = Visibility.Visible;
        }

        #region Progress indicator

        void ShowProgress( )
        {
            mProgressTimer.Stop( );
            mProgressStatus = ProgressStatusEnum.None;

            if( mProgressShownTime != DateTime.MinValue )
            {
#if DEBUG
                Dispatcher.Invoke( ( ) =>
                {
                    Debug.Assert( labelPleaseWait.Visibility == Visibility.Visible );
                } );
#endif
                return;
            }
            else
            {
                mProgressStatus = ProgressStatusEnum.DelayToShow;
                mProgressTimer.Interval = DELAY_BEFORE_PROGRESS;
                mProgressTimer.Start( );
            }
        }

        void HideProgress( bool rightNow = false )
        {
            mProgressTimer.Stop( );
            mProgressStatus = ProgressStatusEnum.None;

            if( rightNow || mProgressShownTime == DateTime.MinValue )
            {
                Dispatcher.Invoke( ( ) => labelPleaseWait.Visibility = Visibility.Hidden );
                mProgressShownTime = DateTime.MinValue;
            }
            else
            {
#if DEBUG
                Dispatcher.Invoke( ( ) =>
                {
                    Debug.Assert( labelPleaseWait.Visibility == Visibility.Visible );
                } );
#endif

                TimeSpan elapsed = DateTime.Now - mProgressShownTime;

                if( elapsed >= MIN_DURATION_PROGRESS )
                {
                    Dispatcher.Invoke( ( ) => labelPleaseWait.Visibility = Visibility.Hidden );
                    mProgressShownTime = DateTime.MinValue;
                }
                else
                {
                    mProgressStatus = ProgressStatusEnum.DelayToHide;
                    mProgressTimer.Interval = MIN_DURATION_PROGRESS - elapsed;
                    mProgressTimer.Start( );
                }
            }
        }

        private void ProgressTimer_Tick( object? sender, EventArgs e )
        {
            mProgressTimer.Stop( );

            switch( mProgressStatus )
            {
            case ProgressStatusEnum.DelayToShow:
                labelPleaseWait.Visibility = Visibility.Visible;
                mProgressShownTime = DateTime.Now;
                break;
            case ProgressStatusEnum.DelayToHide:
                labelPleaseWait.Visibility = Visibility.Hidden;
                mProgressShownTime = DateTime.MinValue;
                break;
            case ProgressStatusEnum.None:
                //
                break;
            default:
                Debug.Assert( false );
                break;
            }

            mProgressStatus = ProgressStatusEnum.None;
        }

        #endregion

        [GeneratedRegex( """
            (?xni)
            ^
            \s* \[? \s*
            (?<first>[\-\+]?\d+) (\s* ([,;]|\s+) \s* (?<next>[\-\+]?\d+))* [,;]?
            \s* \]? \s*
            $
            """, RegexOptions.IgnorePatternWhitespace
        )]
        private static partial Regex RegexToParseContinuedFraction( );

    }
}
